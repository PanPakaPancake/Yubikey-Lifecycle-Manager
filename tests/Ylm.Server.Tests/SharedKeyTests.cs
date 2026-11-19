using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ylm.Agent.Core;
using Ylm.Contracts;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Graph;

namespace Ylm.Server.Tests;

/// <summary>V1.1: geteilter Key mit Verwahrer, Nacherfassen; V2: Vier-Augen-Freigabe für Breakglass.</summary>
public sealed class SharedKeyTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private FakeGraph _graph = null!;
    private HttpClient _owner = null!;
    private Guid _tenantId;

    public async Task InitializeAsync()
    {
        _graph = new FakeGraph();
        _graph.Users.Add(new GraphUser("u-anna", "anna@contoso.com", "Anna Beispiel", "anna@contoso.com", "Produktion", true));
        _graph.Users.Add(new GraphUser("u-ben", "ben@contoso.com", "Ben Muster", "ben@contoso.com", "Produktion", true));
        _graph.Users.Add(new GraphUser("u-carla", "carla@contoso.com", "Carla Selbst", null, "Produktion", true));
        _graph.Users.Add(new GraphUser("u-bg1", "bg-admin1@contoso.com", "Breakglass 1", null, null, true));
        _graph.Users.Add(new GraphUser("u-bg2", "bg-admin2@contoso.com", "Breakglass 2", null, null, true));
        _app.Graph.Tenants[_graph.TenantId] = _graph;
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var s = await db.Installation.FirstAsync();
            s.OrganizationName = "Muster IT";
            s.ClientId = Guid.NewGuid();
            s.HomeTenantId = Guid.NewGuid();
            var t = new Tenant { EntraTenantId = _graph.TenantId, DisplayName = "Contoso GmbH", DefaultDomain = "contoso.com" };
            db.Tenants.Add(t);
            db.Profiles.Add(new RegistrationProfile { Name = "Standard", IsDefault = true });
            await db.SaveChangesAsync();
            _tenantId = t.Id;
        });
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<(AgentConnection Agent, Guid AgentId)> PairAgentAsync(HttpClient client, SimulatedKeyDevice device)
    {
        var code = (await Json(await client.PostAsync("/api/agents/pairing-codes", null))).GetProperty("code").GetString()!;
        var agent = _app.CreateAgent(device);
        var (ok, message) = await agent.PairAsync("https://ylm.test", code, CancellationToken.None);
        Assert.True(ok, message);
        for (var i = 0; i < 100 && agent.Status.Connection != BackendConnectionState.Connected; i++) await Task.Delay(100);
        var agents = await Json(await client.GetAsync("/api/agents"));
        return (agent, agents.EnumerateArray().First(a => a.GetProperty("online").GetBoolean()).GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> WaitForJobAsync(HttpClient client, JsonElement job)
    {
        var id = job.GetProperty("id").GetGuid();
        for (var i = 0; i < 200; i++)
        {
            var current = await Json(await client.GetAsync($"/api/jobs/{id}"));
            if (current.GetProperty("state").GetString() != "Running") return current;
            await Task.Delay(100);
        }
        throw new TimeoutException("job did not finish");
    }

    private async Task<Guid> ProfileIdAsync(HttpClient c) =>
        (await Json(await c.GetAsync($"/api/profiles?tenantId={_tenantId}"))).EnumerateArray().First(p => p.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();

    [Fact]
    public async Task Shared_key_registers_several_accounts_with_custodian_and_adds_more_later()
    {
        var device = new SimulatedKeyDevice { SerialNumber = 40_000_001 };
        var (agent, agentId) = await PairAgentAsync(_owner, device);
        await using var _ = agent;
        var profileId = await ProfileIdAsync(_owner);

        var preview = await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations/preview",
            new { entraUserIds = new[] { "u-anna", "u-ben" }, profileId, agentId, usage = "Shared" }));
        Assert.Equal(2, preview.GetProperty("users").GetArrayLength());
        Assert.Equal(100, preview.GetProperty("remainingSlots").GetInt32());
        Assert.False(preview.GetProperty("needsCurrentPin").GetBoolean());
        Assert.Empty(preview.GetProperty("problems").EnumerateArray());

        // Verwahrer ist Pflicht.
        var noCustodian = await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations",
            new { entraUserIds = new[] { "u-anna", "u-ben" }, profileId, agentId, usage = "Shared", custodian = " ", performReset = false });
        Assert.Equal(HttpStatusCode.BadRequest, noCustodian.StatusCode);

        var job = await WaitForJobAsync(_owner, await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations",
            new { entraUserIds = new[] { "u-anna", "u-ben" }, profileId, agentId, usage = "Shared", custodian = "Dieter Leiter (Produktion)", performReset = false, expectedSerialNumber = 40_000_001 })));
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", job.GetRawText());
        Assert.Equal(2, device.CredentialCount);
        Assert.True(device.ForcePinChangeActive);
        var pdf = await _owner.GetAsync($"/api/pin-letters/{job.GetProperty("pinLetterId").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        var keyId = job.GetProperty("keyId").GetGuid();
        var key = (await Json(await _owner.GetAsync($"/api/keys/{keyId}"))).GetProperty("key");
        Assert.Equal("Shared", key.GetProperty("usage").GetString());
        Assert.Equal("Dieter Leiter (Produktion)", key.GetProperty("custodian").GetString());
        Assert.Equal(2, key.GetProperty("registrations").EnumerateArray().Count(r => r.GetProperty("removedAt").ValueKind == JsonValueKind.Null));

        // Der normale Wizard lehnt den geteilten Key ab.
        var single = await WaitForJobAsync(_owner, await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/registrations",
            new { entraUserId = "u-carla", profileId, agentId, performReset = false })));
        Assert.Equal("Failed", single.GetProperty("state").GetString());

        // Später kommt Carla dazu: PIN ist gesetzt, also wird die aktuelle PIN gebraucht.
        var preview2 = await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations/preview",
            new { entraUserIds = new[] { "u-carla", "u-anna" }, profileId, agentId, usage = "Shared" }));
        Assert.True(preview2.GetProperty("needsCurrentPin").GetBoolean());
        Assert.Contains(preview2.GetProperty("problems").EnumerateArray(), p => p.GetString()!.Contains("anna@contoso.com"));

        var missingPin = await WaitForJobAsync(_owner, await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations",
            new { entraUserIds = new[] { "u-carla" }, profileId, agentId, usage = "Shared", custodian = "Dieter Leiter (Produktion)", performReset = false })));
        Assert.Equal("pin_required", missingPin.GetProperty("errorCode").GetString());
        var wrongPin = await WaitForJobAsync(_owner, await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations",
            new { entraUserIds = new[] { "u-carla" }, profileId, agentId, usage = "Shared", custodian = "Dieter Leiter (Produktion)", performReset = false, currentPin = "00000000" })));
        Assert.Equal("pin_rejected", wrongPin.GetProperty("errorCode").GetString());
        var added = await WaitForJobAsync(_owner, await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations",
            new { entraUserIds = new[] { "u-carla" }, profileId, agentId, usage = "Shared", custodian = "Dieter Leiter (Produktion)", performReset = false, currentPin = device.CurrentPin })));
        Assert.True(added.GetProperty("state").GetString() == "Succeeded", added.GetRawText());
        Assert.Equal(JsonValueKind.Null, added.GetProperty("pinLetterId").ValueKind);
        Assert.Equal(3, device.CredentialCount);

        // Ein Konto entfernen: nur dieser Passkey verschwindet in Entra.
        key = (await Json(await _owner.GetAsync($"/api/keys/{keyId}"))).GetProperty("key");
        var benReg = key.GetProperty("registrations").EnumerateArray().First(r => r.GetProperty("userPrincipalName").GetString() == "ben@contoso.com").GetProperty("id").GetGuid();
        await Json(await _owner.DeleteAsync($"/api/keys/{keyId}/registrations/{benReg}"));
        Assert.Empty(_graph.Methods["u-ben"]);
        Assert.Single(_graph.Methods["u-anna"]);

        // Verwahrer ändern, leer ist nicht erlaubt.
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PatchAsJsonAsync($"/api/keys/{keyId}", new { custodian = "" })).StatusCode);
        var patched = await Json(await _owner.PatchAsJsonAsync($"/api/keys/{keyId}", new { custodian = "Petra Nachfolge" }));
        Assert.Equal("Petra Nachfolge", patched.GetProperty("custodian").GetString());
        var history = (await Json(await _owner.GetAsync($"/api/keys/{keyId}"))).GetProperty("history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("action").GetString() == "key.custodian_changed");
    }

    [Fact]
    public async Task Breakglass_needs_four_eyes_approval_when_tenant_requires_it()
    {
        await Json(await _owner.PatchAsJsonAsync($"/api/tenants/{_tenantId}", new { requireBreakglassApproval = true }));
        var op = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        var device = new SimulatedKeyDevice { SerialNumber = 40_000_100 };
        var (agent, agentId) = await PairAgentAsync(op, device);
        await using var _ = agent;
        var profileId = await ProfileIdAsync(op);
        var users = new[] { "u-bg1", "u-bg2" };

        var preview = await Json(await op.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations/preview", new { entraUserIds = users, profileId, agentId, usage = "Breakglass" }));
        Assert.True(preview.GetProperty("approvalRequired").GetBoolean());
        Assert.Empty(preview.GetProperty("approvals").EnumerateArray());

        var without = await WaitForJobAsync(op, await Json(await op.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations",
            new { entraUserIds = users, profileId, agentId, usage = "Breakglass", custodian = "Tresor IT", performReset = false })));
        Assert.Equal("approval_required", without.GetProperty("errorCode").GetString());
        Assert.Equal(0, device.CredentialCount);

        var approval = await Json(await op.PostAsJsonAsync($"/api/tenants/{_tenantId}/approvals", new { entraUserIds = users, custodian = "Tresor IT", reason = "Notfallkonten für Entra" }));
        var approvalId = approval.GetProperty("id").GetGuid();
        Assert.Equal("Pending", approval.GetProperty("status").GetString());

        // Operator darf nicht freigeben (kein Recht), der Antragsteller schon gar nicht.
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/approvals/{approvalId}/approve", new { note = "ok" })).StatusCode);
        var pending = await WaitForJobAsync(op, await Json(await op.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations",
            new { entraUserIds = users, profileId, agentId, usage = "Breakglass", custodian = "Tresor IT", performReset = false, approvalId })));
        Assert.Equal("approval_not_approved", pending.GetProperty("errorCode").GetString());

        var ownerApproval = await Json(await _owner.PostAsJsonAsync($"/api/approvals/{approvalId}/approve", new { note = "passt" }));
        Assert.Equal("Approved", ownerApproval.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _owner.PostAsJsonAsync($"/api/approvals/{approvalId}/reject", new { note = "doch nicht" })).StatusCode);

        // Nur freigegebene Konten.
        var extra = await WaitForJobAsync(op, await Json(await op.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations",
            new { entraUserIds = new[] { "u-bg1", "u-anna" }, profileId, agentId, usage = "Breakglass", custodian = "Tresor IT", performReset = false, approvalId })));
        Assert.Equal("approval_mismatch", extra.GetProperty("errorCode").GetString());

        var preview2 = await Json(await op.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations/preview", new { entraUserIds = users, profileId, agentId, usage = "Breakglass" }));
        Assert.Single(preview2.GetProperty("approvals").EnumerateArray());
        var done = await WaitForJobAsync(op, await Json(await op.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations",
            new { entraUserIds = users, profileId, agentId, usage = "Breakglass", custodian = "Tresor IT", performReset = false, approvalId })));
        Assert.True(done.GetProperty("state").GetString() == "Succeeded", done.GetRawText());
        Assert.Equal(2, device.CredentialCount);

        var list = await Json(await _owner.GetAsync($"/api/approvals?tenantId={_tenantId}"));
        Assert.Equal("Used", list[0].GetProperty("approval").GetProperty("status").GetString());

        // Eine Freigabe gilt nur einmal.
        device.ReplaceWith(40_000_101);
        var reuse = await WaitForJobAsync(op, await Json(await op.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations",
            new { entraUserIds = users, profileId, agentId, usage = "Breakglass", custodian = "Tresor IT", performReset = false, approvalId })));
        Assert.Equal("approval_not_approved", reuse.GetProperty("errorCode").GetString());

        // Ohne Tenant-Einstellung braucht ein geteilter Key (nicht Breakglass) keine Freigabe.
        var shared = await WaitForJobAsync(op, await Json(await op.PostAsJsonAsync($"/api/tenants/{_tenantId}/shared-registrations",
            new { entraUserIds = new[] { "u-anna" }, profileId, agentId, usage = "Shared", custodian = "Dieter", performReset = false })));
        Assert.True(shared.GetProperty("state").GetString() == "Succeeded", shared.GetRawText());
        var verify = await Json(await _owner.GetAsync("/api/audit/verify"));
        Assert.True(verify.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Self_registered_key_is_identified_with_the_agent_and_merged_with_stock()
    {
        _graph.Methods["u-carla"] = [new GraphFido2Method("m-self", "Mein YubiKey", DateTimeOffset.UtcNow.AddDays(-3), SimulatedKeyDevice.SimulatedAaguid, "YubiKey 5 Series")];
        _graph.Methods["u-ben"] = [new GraphFido2Method("m-ben", "Bens Key", DateTimeOffset.UtcNow.AddDays(-2), SimulatedKeyDevice.SimulatedAaguid, "YubiKey 5 Series")];
        await Json(await _owner.PostAsync($"/api/tenants/{_tenantId}/sync", null));
        var open = await Json(await _owner.GetAsync($"/api/keys?tenantId={_tenantId}&needsDetails=true"));
        Assert.Equal(2, open.GetProperty("total").GetInt32());
        var carlaKey = open.GetProperty("items").EnumerateArray().First(k => k.GetProperty("registrations")[0].GetProperty("userPrincipalName").GetString() == "carla@contoso.com").GetProperty("id").GetGuid();
        var benKey = open.GetProperty("items").EnumerateArray().First(k => k.GetProperty("registrations")[0].GetProperty("userPrincipalName").GetString() == "ben@contoso.com").GetProperty("id").GetGuid();

        var device = new SimulatedKeyDevice { SerialNumber = 40_000_200, Firmware = "5.4.3" };
        var (agent, agentId) = await PairAgentAsync(_owner, device);
        await using var _ = agent;

        // Fall 1: unbekannte Seriennummer, der Eintrag wird ergänzt.
        var r1 = await Json(await _owner.PostAsJsonAsync($"/api/keys/{carlaKey}/identify", new { agentId }));
        Assert.False(r1.GetProperty("merged").GetBoolean());
        Assert.Equal(40_000_200, r1.GetProperty("key").GetProperty("serialNumber").GetInt32());
        Assert.Equal("5.4.3", r1.GetProperty("key").GetProperty("firmwareVersion").GetString());
        Assert.False(r1.GetProperty("key").GetProperty("needsDetails").GetBoolean());
        Assert.NotEmpty(r1.GetProperty("key").GetProperty("advisories").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await _owner.PostAsJsonAsync($"/api/keys/{carlaKey}/identify", new { agentId })).StatusCode);

        // Fall 2: Seriennummer liegt schon im Lager, die Registrierung wird in diesen Eintrag übernommen.
        device.ReplaceWith(40_000_300, "5.7.1");
        await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/keys/intake", new { agentId, tags = new[] { "Lager" } }));
        var r2 = await Json(await _owner.PostAsJsonAsync($"/api/keys/{benKey}/identify", new { agentId }));
        Assert.True(r2.GetProperty("merged").GetBoolean());
        var merged = r2.GetProperty("key");
        Assert.Equal("Active", merged.GetProperty("status").GetString());
        Assert.Contains("Lager", merged.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("ben@contoso.com", merged.GetProperty("registrations")[0].GetProperty("userPrincipalName").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.GetAsync($"/api/keys/{benKey}")).StatusCode);
        var rest = await Json(await _owner.GetAsync($"/api/keys?tenantId={_tenantId}&needsDetails=true"));
        Assert.Equal(0, rest.GetProperty("total").GetInt32());

        // Der nächste Sync legt keinen neuen Platzhalter an.
        var sync = await Json(await _owner.PostAsync($"/api/tenants/{_tenantId}/sync", null));
        Assert.Equal(0, sync.GetProperty("imported").GetInt32());
    }

    [Fact]
    public async Task Identify_refuses_a_key_of_another_model()
    {
        _graph.Methods["u-carla"] = [new GraphFido2Method("m-self", "Mein YubiKey", DateTimeOffset.UtcNow, YubicoAaguids.Known.Keys.First(k => k != SimulatedKeyDevice.SimulatedAaguid), "YubiKey Bio")];
        await Json(await _owner.PostAsync($"/api/tenants/{_tenantId}/sync", null));
        var keyId = (await Json(await _owner.GetAsync($"/api/keys?tenantId={_tenantId}&needsDetails=true"))).GetProperty("items")[0].GetProperty("id").GetGuid();
        var device = new SimulatedKeyDevice { SerialNumber = 40_000_400 };
        var (agent, agentId) = await PairAgentAsync(_owner, device);
        await using var _ = agent;
        var res = await _owner.PostAsJsonAsync($"/api/keys/{keyId}/identify", new { agentId });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("model_mismatch", await res.Content.ReadAsStringAsync());
        await Json(await _owner.PostAsJsonAsync($"/api/keys/{keyId}/identify", new { agentId, ignoreModelMismatch = true }));
    }
}
