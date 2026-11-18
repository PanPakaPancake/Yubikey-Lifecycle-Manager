using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Ylm.Agent.Core;
using Ylm.Contracts;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Graph;

namespace Ylm.Server.Tests;

public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private FakeGraph _graph = null!;
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        _graph = new FakeGraph();
        _graph.Users.Add(new GraphUser("u-anna", "anna@contoso.com", "Anna Beispiel", "anna@contoso.com", "Vertrieb", true));
        _graph.Users.Add(new GraphUser("u-ben", "ben@contoso.com", "Ben Muster", "ben@contoso.com", "IT", true));
        _graph.Users.Add(new GraphUser("u-carla", "carla@contoso.com", "Carla Selbst", null, null, true));
        _app.Graph.Tenants[_graph.TenantId] = _graph;
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var s = await db.Installation.FirstAsync();
            s.OrganizationName = "Muster IT";
            s.ClientId = Guid.NewGuid();
            s.HomeTenantId = Guid.NewGuid();
            await db.SaveChangesAsync();
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
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<Guid> OnboardTenantAsync()
    {
        var consent = await Json(await _owner.PostAsJsonAsync("/api/tenants/consent", new { tenantHint = (string?)null }));
        var url = new Uri(consent.GetProperty("url").GetString()!);
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("https://ylm.test/api/tenants/consent-callback", query["redirect_uri"]);
        var callback = await _owner.GetAsync($"/api/tenants/consent-callback?tenant={_graph.TenantId}&admin_consent=True&state={Uri.EscapeDataString(query["state"]!)}");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var location = callback.Headers.Location!.ToString();
        Assert.StartsWith("/tenants/", location);
        return Guid.Parse(location.Split('/')[2].Split('?')[0]);
    }

    private async Task<(AgentConnection Agent, Guid AgentId)> PairAgentAsync(HttpClient client, SimulatedKeyDevice device)
    {
        var code = (await Json(await client.PostAsync("/api/agents/pairing-codes", null))).GetProperty("code").GetString()!;
        var agent = _app.CreateAgent(device);
        var (ok, message) = await agent.PairAsync("https://ylm.test", code, CancellationToken.None);
        Assert.True(ok, message);
        for (var i = 0; i < 100 && agent.Status.Connection != BackendConnectionState.Connected; i++) await Task.Delay(100);
        Assert.Equal(BackendConnectionState.Connected, agent.Status.Connection);
        var agents = await Json(await client.GetAsync("/api/agents"));
        var entry = agents.EnumerateArray().First(a => a.GetProperty("online").GetBoolean());
        return (agent, entry.GetProperty("id").GetGuid());
    }

    private static void AssertSucceeded(JsonElement job) =>
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", job.GetRawText());

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

    [Fact]
    public async Task Onboarding_checks_and_fix_now()
    {
        _graph.Policy = new Fido2Policy { Enabled = false, KeyRestrictionsEnforced = true, EnforcementType = "allow", AaGuids = [], IncludeTargets = [] };
        var tenantId = await OnboardTenantAsync();
        var tenant = await Json(await _owner.GetAsync($"/api/tenants/{tenantId}"));
        Assert.Equal("Contoso GmbH", tenant.GetProperty("displayName").GetString());
        var failed = tenant.GetProperty("checks").EnumerateArray().Where(c => c.GetProperty("status").GetString() == "Failed").Select(c => c.GetProperty("id").GetString()!).ToList();
        Assert.Equal(3, failed.Count);

        var fixedTenant = await Json(await _owner.PostAsJsonAsync($"/api/tenants/{tenantId}/fix", new { checkIds = failed }));
        Assert.All(fixedTenant.GetProperty("checks").EnumerateArray(), c => Assert.Equal("Ok", c.GetProperty("status").GetString()));
        Assert.True(_graph.Policy.Enabled);
        Assert.Equal(1, _graph.PolicyUpdates);

        _graph.GrantedPermissions.Remove("Policy.ReadWrite.AuthenticationMethod");
        var rechecked = await Json(await _owner.PostAsync($"/api/tenants/{tenantId}/checks", null));
        Assert.Contains(rechecked.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "graph-permissions" && c.GetProperty("status").GetString() == "Failed");
    }

    [Fact]
    public async Task Recheck_uses_fresh_token_after_consent_became_effective()
    {
        // Token issued before the consent was effective: no roles.
        _graph.SimulateTokenCache = true;
        _graph.TokenRoles = [];
        var tenantId = await OnboardTenantAsync();
        var tenant = await Json(await _owner.GetAsync($"/api/tenants/{tenantId}"));
        Assert.Contains(tenant.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "graph-permissions" && c.GetProperty("status").GetString() == "Ok");

        // Permissions not yet effective at onboarding: tenant gets the id as name, the check fails.
        _graph.TokenRoles = [];
        _graph.GrantedPermissions = [];
        await _app.WithDbAsync(async db =>
        {
            var t = await db.Tenants.FindAsync(tenantId);
            t!.DisplayName = t.EntraTenantId.ToString();
            t.DefaultDomain = null;
            await db.SaveChangesAsync();
        });
        var failed = await Json(await _owner.PostAsync($"/api/tenants/{tenantId}/checks", null));
        Assert.Contains(failed.GetProperty("checks").EnumerateArray(), c => c.GetProperty("id").GetString() == "graph-permissions" && c.GetProperty("status").GetString() == "Failed");

        // Consent becomes effective; the cached token still has no roles. "Erneut prüfen" must fetch a new one.
        _graph.GrantedPermissions = [.. Ylm.Server.Setup.SetupService.RequiredApplicationPermissions];
        var ok = await Json(await _owner.PostAsync($"/api/tenants/{tenantId}/checks", null));
        Assert.All(ok.GetProperty("checks").EnumerateArray().Where(c => c.GetProperty("id").GetString() == "graph-permissions"),
            c => Assert.Equal("Ok", c.GetProperty("status").GetString()));
        Assert.Equal("Contoso GmbH", ok.GetProperty("displayName").GetString());
        Assert.Equal("contoso.com", ok.GetProperty("defaultDomain").GetString());
    }

    [Fact]
    public async Task Single_wizard_registers_key_with_pin_letter_and_lifecycle()
    {
        var tenantId = await OnboardTenantAsync();
        var device = new SimulatedKeyDevice { SerialNumber = 20_000_001 };
        var operatorClient = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        var (agent, agentId) = await PairAgentAsync(operatorClient, device);
        await using var _ = agent;

        var profiles = await Json(await operatorClient.GetAsync($"/api/profiles?tenantId={tenantId}"));
        var profileId = profiles.EnumerateArray().First(p => p.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();

        var users = await Json(await operatorClient.GetAsync($"/api/tenants/{tenantId}/users?q=anna"));
        Assert.Equal("u-anna", users[0].GetProperty("id").GetString());

        var preview = await Json(await operatorClient.PostAsJsonAsync($"/api/tenants/{tenantId}/registrations/preview", new { entraUserId = "u-anna", profileId, agentId }));
        Assert.Empty(preview.GetProperty("violations").EnumerateArray());
        Assert.True(preview.GetProperty("willForcePinChange").GetBoolean());
        Assert.Equal("YubiKey 20000001", preview.GetProperty("displayName").GetString());

        var job = await Json(await operatorClient.PostAsJsonAsync($"/api/tenants/{tenantId}/registrations",
            new { entraUserId = "u-anna", profileId, agentId, overrides = Array.Empty<string>(), performReset = false, expectedSerialNumber = 20_000_001 }));
        var done = await WaitForJobAsync(operatorClient, job);
        AssertSucceeded(done);

        // Graph received a valid credential, the key forces a PIN change.
        Assert.Single(_graph.Registered);
        Assert.True(device.ForcePinChangeActive);
        Assert.NotNull(device.CurrentPin);
        Assert.Equal(8, device.CurrentPin!.Length);

        // PIN letter: PDF, never stored in the database.
        var pdf = await operatorClient.GetAsync($"/api/pin-letters/{done.GetProperty("pinLetterId").GetGuid()}");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        await _app.WithDbAsync(async db =>
        {
            var conn = db.Database.GetDbConnection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM audit_log WHERE \"Details\" LIKE @p";
            var p = cmd.CreateParameter(); p.ParameterName = "p"; p.Value = "%" + device.CurrentPin + "%"; cmd.Parameters.Add(p);
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync())!);
        });

        var keyId = done.GetProperty("keyId").GetGuid();
        var key = (await Json(await operatorClient.GetAsync($"/api/keys/{keyId}"))).GetProperty("key");
        Assert.Equal("Active", key.GetProperty("status").GetString());
        Assert.Equal(20_000_001, key.GetProperty("serialNumber").GetInt32());
        Assert.True(key.GetProperty("pinChangeForced").GetBoolean());
        Assert.Equal("anna@contoso.com", key.GetProperty("registrations")[0].GetProperty("userPrincipalName").GetString());

        // Second registration of the same key without reset is refused.
        var again = await WaitForJobAsync(operatorClient, await Json(await operatorClient.PostAsJsonAsync($"/api/tenants/{tenantId}/registrations",
            new { entraUserId = "u-ben", profileId, agentId, performReset = false })));
        Assert.Equal("Failed", again.GetProperty("state").GetString());

        // Helpdesk locks the key: passkey deleted in Entra.
        var helpdesk = await _app.LoginAsync("hd@home.example", PlatformRole.Helpdesk);
        var regDenied = await helpdesk.PostAsJsonAsync($"/api/tenants/{tenantId}/registrations", new { entraUserId = "u-ben", profileId, agentId, performReset = false });
        Assert.Equal(HttpStatusCode.Forbidden, regDenied.StatusCode);
        var locked = await Json(await helpdesk.PostAsJsonAsync($"/api/keys/{keyId}/lock", new { reason = "Lost", note = "Im Zug verloren" }));
        Assert.Equal("Locked", locked.GetProperty("status").GetString());
        Assert.Empty(_graph.Methods["u-anna"]);

        var auditor = await _app.LoginAsync("audit@home.example", PlatformRole.Auditor);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync($"/api/keys/{keyId}/retire", new { note = "x" })).StatusCode);

        // Operator resets the key back to stock (simulated replug).
        var resetJob = await WaitForJobAsync(operatorClient, await Json(await operatorClient.PostAsJsonAsync($"/api/keys/{keyId}/reset", new { agentId })));
        AssertSucceeded(resetJob);
        Assert.Null(device.CurrentPin);
        key = (await Json(await operatorClient.GetAsync($"/api/keys/{keyId}"))).GetProperty("key");
        Assert.Equal("InStock", key.GetProperty("status").GetString());

        // Re-issue to Ben with an old-firmware key: override must be confirmed explicitly.
        device.Firmware = "5.4.3";
        var preview2 = await Json(await operatorClient.PostAsJsonAsync($"/api/tenants/{tenantId}/registrations/preview", new { entraUserId = "u-ben", profileId, agentId }));
        Assert.Contains(preview2.GetProperty("violations").EnumerateArray(), v => v.GetProperty("code").GetString() == "ForcePinChangeUnsupported");
        var refused = await WaitForJobAsync(operatorClient, await Json(await operatorClient.PostAsJsonAsync($"/api/tenants/{tenantId}/registrations",
            new { entraUserId = "u-ben", profileId, agentId, performReset = false })));
        Assert.Equal("Failed", refused.GetProperty("state").GetString());
        Assert.Equal("profile_violation", refused.GetProperty("errorCode").GetString());
        var accepted = await WaitForJobAsync(operatorClient, await Json(await operatorClient.PostAsJsonAsync($"/api/tenants/{tenantId}/registrations",
            new { entraUserId = "u-ben", profileId, agentId, overrides = new[] { "ForcePinChangeUnsupported" }, performReset = false })));
        AssertSucceeded(accepted);
        Assert.False(device.ForcePinChangeActive);
        var history = (await Json(await operatorClient.GetAsync($"/api/keys/{keyId}"))).GetProperty("history");
        Assert.Contains(history.EnumerateArray(), h => h.GetProperty("action").GetString() == "key.profile_override");

        // Audit chain is intact.
        var verify = await Json(await _owner.GetAsync("/api/audit/verify"));
        Assert.True(verify.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Sync_imports_self_registered_keys_and_reports()
    {
        var tenantId = await OnboardTenantAsync();
        var aaguid = YubicoAaguids.Known.Keys.First();
        _graph.Methods["u-carla"] = [new GraphFido2Method("m-self", "Mein YubiKey", DateTimeOffset.UtcNow.AddDays(-3), aaguid, "YubiKey 5 Series")];
        _graph.Methods["u-ben"] = [new GraphFido2Method("m-other", "Fremder Key", DateTimeOffset.UtcNow, Guid.NewGuid(), "Feitian ePass")];

        // The person search shows passkeys straight from Entra, even before a sync.
        var found = await Json(await _owner.GetAsync($"/api/tenants/{tenantId}/users?q=carla"));
        var carla = found.EnumerateArray().First(u => u.GetProperty("id").GetString() == "u-carla");
        Assert.Equal(0, carla.GetProperty("activeKeys").GetInt32());
        var passkey = Assert.Single(carla.GetProperty("entraPasskeys").EnumerateArray());
        Assert.True(passkey.GetProperty("isYubiKey").GetBoolean());

        var result = await Json(await _owner.PostAsync($"/api/tenants/{tenantId}/sync", null));
        Assert.Equal(1, result.GetProperty("imported").GetInt32());

        var keys = await Json(await _owner.GetAsync($"/api/keys?tenantId={tenantId}"));
        var key = Assert.Single(keys.GetProperty("items").EnumerateArray());
        Assert.Equal("EntraSync", key.GetProperty("source").GetString());
        Assert.True(key.GetProperty("needsDetails").GetBoolean());

        // Stock import via CSV with tags.
        var csv = new StringContent("Seriennummer;Modell;Firmware;Tags\n30000001;YubiKey 5C NFC;5.7.1;Berlin,neu\n30000002;YubiKey 5 NFC;5.4.3;\nabc;x;y;\n", System.Text.Encoding.UTF8, "text/csv");
        var import = await Json(await _owner.PostAsync($"/api/tenants/{tenantId}/keys/import", csv));
        Assert.Equal(2, import.GetProperty("created").GetInt32());
        Assert.Single(import.GetProperty("errors").EnumerateArray());

        var dashboard = await Json(await _owner.GetAsync($"/api/dashboard?tenantId={tenantId}"));
        Assert.Equal(2, dashboard.GetProperty("inStock").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("usersWithKey").GetInt32());
        Assert.Equal(1, dashboard.GetProperty("vulnerableKeys").GetInt32());

        var exportCsv = await (await _owner.GetAsync($"/api/exports/keys.csv?tenantId={tenantId}")).Content.ReadAsStringAsync();
        Assert.Contains("30000001", exportCsv);
        var reportPdf = await _owner.GetAsync($"/api/exports/dashboard.pdf?tenantId={tenantId}");
        Assert.Equal("application/pdf", reportPdf.Content.Headers.ContentType!.MediaType);

        // Removing the passkey in Entra retires the sync-only key on the next sync.
        _graph.Methods["u-carla"].Clear();
        var second = await Json(await _owner.PostAsync($"/api/tenants/{tenantId}/sync", null));
        Assert.Equal(1, second.GetProperty("removed").GetInt32());
    }

    private async Task<Guid> AddSecondTenantAsync(string name = "Fabrikam")
    {
        var other = new FakeGraph();
        _app.Graph.Tenants[other.TenantId] = other;
        var id = Guid.Empty;
        await _app.WithDbAsync(async db =>
        {
            var t = new Tenant { EntraTenantId = other.TenantId, DisplayName = name, DefaultDomain = name.ToLower() + ".com" };
            db.Tenants.Add(t);
            await db.SaveChangesAsync();
            id = t.Id;
        });
        return id;
    }

    [Fact]
    public async Task Read_api_keys_are_global_and_optionally_restricted_to_tenants()
    {
        var tenantId = await OnboardTenantAsync();
        var otherId = await AddSecondTenantAsync();
        await Json(await _owner.PostAsync($"/api/tenants/{tenantId}/keys/import",
            new StringContent("Seriennummer\n40000001\n", System.Text.Encoding.UTF8, "text/csv")));
        await Json(await _owner.PostAsync($"/api/tenants/{otherId}/keys/import",
            new StringContent("Seriennummer\n50000001\n50000002\n", System.Text.Encoding.UTF8, "text/csv")));

        // Restricted to one tenant.
        var restricted = await Json(await _owner.PostAsJsonAsync("/api/api-keys", new { name = "Ticketsystem", allTenants = false, tenantIds = new[] { tenantId } }));
        var anon = _app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/keys")).StatusCode);
        anon.DefaultRequestHeaders.Add("X-Api-Key", restricted.GetProperty("apiKey").GetString()!);
        var page = await Json(await anon.GetAsync("/api/v1/keys?status=InStock"));
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal("40000001", page.GetProperty("items")[0].GetProperty("serialNumber").GetString());
        Assert.Equal(tenantId.ToString(), page.GetProperty("items")[0].GetProperty("tenantId").GetString());
        Assert.Single((await Json(await anon.GetAsync("/api/v1/tenants"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync($"/api/v1/keys?tenant={otherId}")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await anon.PostAsync("/api/v1/keys", null)).StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound ? HttpStatusCode.MethodNotAllowed : HttpStatusCode.OK);

        // Valid for all tenants, filter by domain.
        var all = await Json(await _owner.PostAsJsonAsync("/api/api-keys", new { name = "Reporting", allTenants = true }));
        var allClient = _app.Client();
        allClient.DefaultRequestHeaders.Add("X-Api-Key", all.GetProperty("apiKey").GetString()!);
        Assert.Equal(3, (await Json(await allClient.GetAsync("/api/v1/keys"))).GetProperty("total").GetInt32());
        Assert.Equal(2, (await Json(await allClient.GetAsync("/api/v1/keys?tenant=fabrikam.com"))).GetProperty("total").GetInt32());
        var summary = await Json(await allClient.GetAsync("/api/v1/summary"));
        Assert.Equal(3, summary.GetProperty("total").GetInt32());
        Assert.Equal("Alle (2 Tenants)", summary.GetProperty("tenant").GetString());

        // Scope can be changed later.
        var restrictedId = restricted.GetProperty("id").GetGuid();
        (await _owner.PutAsJsonAsync($"/api/api-keys/{restrictedId}/scope", new { allTenants = false, tenantIds = new[] { tenantId, otherId } })).EnsureSuccessStatusCode();
        Assert.Equal(3, (await Json(await anon.GetAsync("/api/v1/keys"))).GetProperty("total").GetInt32());
        var listed = await Json(await _owner.GetAsync("/api/api-keys"));
        Assert.Equal(2, listed.EnumerateArray().First(k => k.GetProperty("id").GetGuid() == restrictedId).GetProperty("tenants").GetArrayLength());

        var openapi = await Json(await _app.Client().GetAsync("/openapi/v1.json"));
        Assert.True(openapi.GetProperty("paths").TryGetProperty("/api/v1/keys", out _));
        Assert.False(openapi.GetProperty("paths").TryGetProperty("/api/tenants", out _));

        // Operators cannot manage API keys.
        await Json(await _owner.PostAsJsonAsync("/api/users", new { upn = "op@home.example", role = "Operator", allTenants = true, tenantIds = Array.Empty<Guid>() }));
        var op = await _app.LoginAsync("op@home.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/api-keys")).StatusCode);

        await _owner.DeleteAsync($"/api/api-keys/{restrictedId}");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/keys")).StatusCode);
    }

    [Fact]
    public async Task Global_custom_fields_are_inherited_and_can_be_changed_or_disabled_per_tenant()
    {
        var tenantId = await OnboardTenantAsync();
        var otherId = await AddSecondTenantAsync();
        (await _owner.PutAsJsonAsync("/api/fields", new object[]
        {
            new { key = "kostenstelle", label = "Kostenstelle", type = "Text", required = false, options = Array.Empty<string>() },
            new { key = "standort", label = "Standort", type = "Select", required = false, options = new[] { "Berlin", "Köln" } },
        })).EnsureSuccessStatusCode();

        var config = await Json(await _owner.GetAsync($"/api/tenants/{tenantId}/fields/config"));
        Assert.All(config.EnumerateArray(), f => Assert.Equal("Inherited", f.GetProperty("state").GetString()));
        var standortId = config.EnumerateArray().First(f => f.GetProperty("key").GetString() == "standort").GetProperty("id").GetGuid();
        var kostenstelleId = config.EnumerateArray().First(f => f.GetProperty("key").GetString() == "kostenstelle").GetProperty("id").GetGuid();

        // Local key may not shadow a global one.
        var clash = await _owner.PutAsJsonAsync($"/api/tenants/{tenantId}/fields", new
        {
            local = new[] { new { key = "standort", label = "X", type = "Text", required = false, options = Array.Empty<string>() } },
            overrides = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);

        (await _owner.PutAsJsonAsync($"/api/tenants/{tenantId}/fields", new
        {
            local = new[] { new { key = "raum", label = "Raum", type = "Text", required = false, options = Array.Empty<string>() } },
            overrides = new object[]
            {
                new { fieldId = standortId, disabled = false, label = "Werk", required = true, options = new[] { "Hamburg" } },
                new { fieldId = kostenstelleId, disabled = true },
            },
        })).EnsureSuccessStatusCode();

        config = await Json(await _owner.GetAsync($"/api/tenants/{tenantId}/fields/config"));
        var states = config.EnumerateArray().ToDictionary(f => f.GetProperty("key").GetString()!, f => (f.GetProperty("state").GetString(), f.GetProperty("origin").GetString()));
        Assert.Equal(("Disabled", "Global"), states["kostenstelle"]);
        Assert.Equal(("Modified", "Global"), states["standort"]);
        Assert.Equal(("Local", "Tenant"), states["raum"]);
        var active = await Json(await _owner.GetAsync($"/api/tenants/{tenantId}/fields"));
        Assert.Equal(["standort", "raum"], active.EnumerateArray().Select(f => f.GetProperty("key").GetString()));

        // The other tenant still inherits everything unchanged.
        var otherActive = await Json(await _owner.GetAsync($"/api/tenants/{otherId}/fields"));
        Assert.Equal(["kostenstelle", "standort"], otherActive.EnumerateArray().Select(f => f.GetProperty("key").GetString()));

        // Validation follows the tenant's version of the field.
        var bad = await _owner.PostAsync($"/api/tenants/{tenantId}/keys/import", new StringContent("Seriennummer;standort\n60000001;Berlin\n", System.Text.Encoding.UTF8, "text/csv"));
        Assert.Contains("Hamburg", await bad.Content.ReadAsStringAsync());

        // A global key that already exists locally in a tenant is rejected.
        var conflict = await _owner.PutAsJsonAsync("/api/fields", new object[] { new { key = "raum", label = "Raum", type = "Text", required = false, options = Array.Empty<string>() } });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("Contoso", await conflict.Content.ReadAsStringAsync());

        var global = await Json(await _owner.GetAsync("/api/fields"));
        var st = global.EnumerateArray().First(f => f.GetProperty("key").GetString() == "standort");
        Assert.Equal(1, st.GetProperty("tenantsModified").GetInt32());
    }

    [Fact]
    public async Task Target_audience_supports_several_groups_and_exclusions()
    {
        var tenantId = await OnboardTenantAsync();
        _graph.GroupMembers["g-vertrieb"] = ["u-anna", "u-ben"];
        _graph.GroupMembers["g-it"] = ["u-ben", "u-carla"];
        _graph.GroupMembers["g-service"] = ["u-carla"];
        _graph.Policy = new Fido2Policy { Enabled = true, IncludeTargets = [new PolicyTarget("g-vertrieb")], ExcludeTargets = [new PolicyTarget("g-it")] };

        var t = await Json(await _owner.PatchAsJsonAsync($"/api/tenants/{tenantId}", new
        {
            targetIncludeGroups = new[] { new { id = "g-vertrieb", name = "Vertrieb" }, new { id = "g-it", name = "IT" } },
            targetExcludeGroups = new[] { new { id = "g-service", name = "Dienstkonten" } },
        }));
        Assert.Equal(2, t.GetProperty("targetIncludeGroups").GetArrayLength());

        var both = await _owner.PatchAsJsonAsync($"/api/tenants/{tenantId}", new
        {
            targetIncludeGroups = new[] { new { id = "g-it", name = "IT" } },
            targetExcludeGroups = new[] { new { id = "g-it", name = "IT" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        var checks = await Json(await _owner.PostAsync($"/api/tenants/{tenantId}/checks", null));
        var target = checks.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == TenantCheckIds.TargetGroup);
        Assert.Equal("Failed", target.GetProperty("status").GetString());
        Assert.Contains("IT", target.GetProperty("message").GetString());

        var fixedTenant = await Json(await _owner.PostAsJsonAsync($"/api/tenants/{tenantId}/fix", new { checkIds = new[] { TenantCheckIds.TargetGroup } }));
        Assert.Equal("Ok", fixedTenant.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("id").GetString() == TenantCheckIds.TargetGroup).GetProperty("status").GetString());
        Assert.Contains(_graph.Policy.IncludeTargets, x => x.Id == "g-it");
        Assert.DoesNotContain(_graph.Policy.ExcludeTargets, x => x.Id == "g-it");

        // (Vertrieb ∪ IT) \ Dienstkonten = Anna, Ben
        var sync = await Json(await _owner.PostAsync($"/api/tenants/{tenantId}/sync", null));
        Assert.Equal(2, sync.GetProperty("targetUsers").GetInt32());
    }

    [Fact]
    public async Task Group_profiles_suggest_the_profile_for_a_person()
    {
        var tenantId = await OnboardTenantAsync();
        _graph.GroupMembers["g-prod"] = ["u-ben"];
        var profile = await Json(await _owner.PostAsJsonAsync("/api/profiles", new
        {
            tenantId, name = "Produktion", pinStrategy = "Random", pinLength = 8, forcePinChange = false, factoryResetBeforeRegistration = false, isDefault = false,
        }));
        var profileId = profile.GetProperty("id").GetGuid();
        (await _owner.PutAsJsonAsync($"/api/tenants/{tenantId}/group-profiles", new[] { new { groupId = "g-prod", groupName = "Produktion", profileId } })).EnsureSuccessStatusCode();

        var list = await Json(await _owner.GetAsync($"/api/tenants/{tenantId}/group-profiles"));
        Assert.Equal("Produktion", Assert.Single(list.EnumerateArray()).GetProperty("profileName").GetString());

        var ben = await Json(await _owner.GetAsync($"/api/tenants/{tenantId}/users/u-ben/suggested-profile"));
        Assert.Equal(profileId, ben.GetProperty("profileId").GetGuid());
        var anna = await Json(await _owner.GetAsync($"/api/tenants/{tenantId}/users/u-anna/suggested-profile"));
        Assert.Equal(JsonValueKind.Null, anna.GetProperty("profileId").ValueKind);

        // Profiles of other tenants cannot be assigned.
        var otherId = await AddSecondTenantAsync();
        var foreign = await Json(await _owner.PostAsJsonAsync("/api/profiles", new
        {
            tenantId = otherId, name = "Fremd", pinStrategy = "Random", pinLength = 8, forcePinChange = false, factoryResetBeforeRegistration = false, isDefault = false,
        }));
        var res = await _owner.PutAsJsonAsync($"/api/tenants/{tenantId}/group-profiles", new[] { new { groupId = "g-x", groupName = "X", profileId = foreign.GetProperty("id").GetGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Tenant_scope_restricts_users()
    {
        var tenantId = await OnboardTenantAsync();
        var other = new FakeGraph();
        _app.Graph.Tenants[other.TenantId] = other;
        await _app.WithDbAsync(async db =>
        {
            db.Tenants.Add(new Tenant { EntraTenantId = other.TenantId, DisplayName = "Fabrikam" });
            await db.SaveChangesAsync();
        });
        var created = await Json(await _owner.PostAsJsonAsync("/api/users", new { upn = "scoped@home.example", role = "Operator", allTenants = false, tenantIds = new[] { tenantId } }));
        Assert.False(created.GetProperty("allTenants").GetBoolean());
        var scoped = await _app.LoginAsync("scoped@home.example");
        var tenants = await Json(await scoped.GetAsync("/api/tenants"));
        Assert.Equal(tenantId, Assert.Single(tenants.EnumerateArray()).GetProperty("id").GetGuid());
        Guid otherId = default;
        await _app.WithDbAsync(async db => otherId = (await db.Tenants.FirstAsync(t => t.DisplayName == "Fabrikam")).Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await scoped.GetAsync($"/api/keys?tenantId={otherId}")).StatusCode);

        // Last owner cannot be demoted.
        var me = await Json(await _owner.GetAsync("/api/me"));
        var res = await _owner.PutAsJsonAsync($"/api/users/{me.GetProperty("user").GetProperty("id").GetGuid()}", new { upn = "owner@home.example", role = "Auditor", allTenants = true });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Audit_chain_detects_tampering_and_csrf_header_is_required()
    {
        var tenantId = await OnboardTenantAsync();
        Assert.True((await Json(await _owner.GetAsync("/api/audit/verify"))).GetProperty("valid").GetBoolean());
        await _app.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE audit_log SET \"Target\" = 'manipuliert' WHERE \"Sequence\" = 2"));
        var verify = await Json(await _owner.GetAsync("/api/audit/verify"));
        Assert.False(verify.GetProperty("valid").GetBoolean());
        Assert.Equal(2, verify.GetProperty("brokenAt").GetInt64());

        var noCsrf = _app.Client();
        noCsrf.DefaultRequestHeaders.Remove("X-YLM-CSRF");
        await noCsrf.PostAsJsonAsync("/auth/dev-login", new { upn = "owner@home.example" });
        var blocked = await noCsrf.PostAsync($"/api/tenants/{tenantId}/sync", null);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
    }

    [Fact]
    public async Task Setup_wizard_requires_token_and_creates_certificate_and_script()
    {
        var anon = _app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/setup/state")).StatusCode);
        string token = "";
        var secrets = (Ylm.Server.Secrets.ISecretStore)_app.Services.GetService(typeof(Ylm.Server.Secrets.ISecretStore))!;
        token = System.Text.Encoding.UTF8.GetString((await secrets.GetAsync("setup-token"))!);
        anon.DefaultRequestHeaders.Add("X-Setup-Token", token);
        (await anon.PostAsJsonAsync("/api/setup/basics", new { organizationName = "Muster IT", publicBaseUrl = "https://ylm.test", bootstrapOwnerUpn = "owner@home.example" })).EnsureSuccessStatusCode();
        var cert = await Json(await anon.PostAsync("/api/setup/certificate", null));
        Assert.Equal(40, cert.GetProperty("thumbprint").GetString()!.Length);
        var script = await (await anon.GetAsync("/api/setup/app-registration.ps1")).Content.ReadAsStringAsync();
        Assert.Contains("New-MgApplication", script);
        Assert.Contains("https://ylm.test/signin-oidc", script);
        Assert.Contains("UserAuthenticationMethod.ReadWrite.All", script);
        Assert.Contains("New-MgServicePrincipalAppRoleAssignment", script);
        Assert.Contains("New-MgOauth2PermissionGrant", script);
    }
}
