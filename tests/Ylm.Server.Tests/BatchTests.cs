using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf.IO;
using Ylm.Agent.Core;
using Ylm.Contracts;
using Ylm.Core;
using Ylm.Server.Graph;

namespace Ylm.Server.Tests;

/// <summary>Stapelregistrierung: list from group/CSV, one key after the other, fixed serials, merged PIN letters.</summary>
public sealed class BatchTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private FakeGraph _graph = null!;
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        _graph = new FakeGraph();
        _graph.Users.Add(new GraphUser("u-anna", "anna@contoso.com", "Anna Beispiel", "anna@contoso.com", "Vertrieb", true));
        _graph.Users.Add(new GraphUser("u-ben", "ben@contoso.com", "Ben Muster", "ben@contoso.com", "Vertrieb", true));
        _graph.Users.Add(new GraphUser("u-carla", "carla@contoso.com", "Carla Selbst", null, "Vertrieb", true));
        _graph.GroupMembers["grp-vertrieb"] = ["u-anna", "u-ben", "u-carla"];
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
        var query = HttpUtility.ParseQueryString(new Uri(consent.GetProperty("url").GetString()!).Query);
        var callback = await _owner.GetAsync($"/api/tenants/consent-callback?tenant={_graph.TenantId}&admin_consent=True&state={Uri.EscapeDataString(query["state"]!)}");
        return Guid.Parse(callback.Headers.Location!.ToString().Split('/')[2].Split('?')[0]);
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
            if (current.GetProperty("state").GetString() != "Running")
            {
                await Task.Delay(100); // the batch item is updated right after the job ends
                return current;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("job did not finish");
    }

    private static object Input(JsonElement c) => new
    {
        entraUserId = c.GetProperty("entraUserId").GetString(),
        userPrincipalName = c.GetProperty("userPrincipalName").GetString(),
        userDisplayName = c.GetProperty("displayName").GetString(),
        department = c.GetProperty("department").GetString(),
        profileId = c.GetProperty("profileId").ValueKind == JsonValueKind.Null ? (Guid?)null : c.GetProperty("profileId").GetGuid(),
        profileGroupName = c.GetProperty("profileGroupName").GetString(),
        expectedSerialNumber = c.GetProperty("expectedSerialNumber").ValueKind == JsonValueKind.Null ? (int?)null : c.GetProperty("expectedSerialNumber").GetInt32(),
    };

    [Fact]
    public async Task Csv_candidates_report_unknown_people_and_bad_serials()
    {
        var tenantId = await OnboardTenantAsync();
        const string csv = "UPN;Seriennummer\r\nanna@contoso.com;30000003\r\nunbekannt@contoso.com;\r\nben@contoso.com;abc\r\ncarla@contoso.com;\r\nanna@contoso.com;\r\n";
        var result = await Json(await _owner.PostAsJsonAsync($"/api/tenants/{tenantId}/batches/candidates", new { csv }));
        var candidates = result.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(["u-anna", "u-carla"], candidates.Select(c => c.GetProperty("entraUserId").GetString()));
        Assert.Equal(30_000_003, candidates[0].GetProperty("expectedSerialNumber").GetInt32());
        var errors = result.GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.GetProperty("line").GetInt32() == 3 && e.GetProperty("message").GetString()!.Contains("nicht gefunden"));
        Assert.Contains(errors, e => e.GetProperty("line").GetInt32() == 4 && e.GetProperty("message").GetString()!.Contains("keine Zahl"));
        Assert.Contains(errors, e => e.GetProperty("line").GetInt32() == 6 && e.GetProperty("message").GetString()!.Contains("mehrfach"));

        // Without header: first column UPN, second serial.
        var plain = await Json(await _owner.PostAsJsonAsync($"/api/tenants/{tenantId}/batches/candidates", new { csv = "ben@contoso.com,30000009\n" }));
        Assert.Equal(30_000_009, plain.GetProperty("candidates")[0].GetProperty("expectedSerialNumber").GetInt32());
    }

    [Fact]
    public async Task Batch_registers_one_key_after_another_and_merges_pin_letters()
    {
        var tenantId = await OnboardTenantAsync();
        var operatorClient = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        var device = new SimulatedKeyDevice { SerialNumber = 30_000_001 };
        var (agent, agentId) = await PairAgentAsync(operatorClient, device);
        await using var _ = agent;

        var profiles = await Json(await operatorClient.GetAsync($"/api/profiles?tenantId={tenantId}"));
        var profileId = profiles.EnumerateArray().First(p => p.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();

        // List from the Entra group; Carla gets a fixed key afterwards.
        var candidates = await Json(await operatorClient.PostAsJsonAsync($"/api/tenants/{tenantId}/batches/candidates", new { groupId = "grp-vertrieb" }));
        var list = candidates.GetProperty("candidates").EnumerateArray().ToList();
        Assert.Equal(["Anna Beispiel", "Ben Muster", "Carla Selbst"], list.Select(c => c.GetProperty("displayName").GetString()));
        var batch = await Json(await operatorClient.PostAsJsonAsync($"/api/tenants/{tenantId}/batches",
            new { name = "Vertrieb Rollout", defaultProfileId = profileId, source = "Gruppe: Vertrieb", items = list.Select(Input) }));
        var batchId = batch.GetProperty("id").GetGuid();
        var items = batch.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("entraUserId").GetString()!, i => i.GetProperty("id").GetGuid());
        await Json(await operatorClient.PatchAsJsonAsync($"/api/batches/{batchId}/items/{items["u-carla"]}", new { expectedSerialNumber = 30_000_003 }));

        // Duplicate people are refused.
        var dup = await operatorClient.PostAsJsonAsync($"/api/batches/{batchId}/items", new { items = new[] { Input(list[0]) } });
        Assert.Equal(HttpStatusCode.BadRequest, dup.StatusCode);

        async Task<JsonElement> RegisterNextAsync(string expectedUser)
        {
            var next = await Json(await operatorClient.PostAsJsonAsync($"/api/batches/{batchId}/next", new { agentId }));
            Assert.Equal("ready", next.GetProperty("state").GetString());
            var item = next.GetProperty("item");
            Assert.Equal(expectedUser, item.GetProperty("entraUserId").GetString());
            var serial = next.GetProperty("preview").GetProperty("key").GetProperty("serialNumber").GetInt32();
            var job = await Json(await operatorClient.PostAsJsonAsync($"/api/batches/{batchId}/items/{item.GetProperty("id").GetGuid()}/register",
                new { agentId, overrides = Array.Empty<string>(), performReset = false, expectedSerialNumber = serial }));
            var done = await WaitForJobAsync(operatorClient, job);
            Assert.True(done.GetProperty("state").GetString() == "Succeeded", done.GetRawText());
            return done;
        }

        // 1st key: next open person without a fixed key (Anna).
        await RegisterNextAsync("u-anna");
        var same = await Json(await operatorClient.PostAsJsonAsync($"/api/batches/{batchId}/next", new { agentId }));
        Assert.Equal("already_done", same.GetProperty("state").GetString());

        // The key reserved for Carla goes to Carla, even though Ben is next in line.
        device.ReplaceWith(30_000_003);
        await Task.Delay(300);
        await RegisterNextAsync("u-carla");

        // Ben gets the next free key.
        device.ReplaceWith(30_000_002);
        await Task.Delay(300);
        await RegisterNextAsync("u-ben");

        var finished = await Json(await operatorClient.GetAsync($"/api/batches/{batchId}"));
        Assert.Equal("Completed", finished.GetProperty("status").GetString());
        Assert.Equal(3, finished.GetProperty("counts").GetProperty("done").GetInt32());
        var carla = finished.GetProperty("items").EnumerateArray().First(i => i.GetProperty("entraUserId").GetString() == "u-carla");
        Assert.Equal(30_000_003, carla.GetProperty("serialNumber").GetInt32());
        Assert.True(carla.GetProperty("pinLetterAvailable").GetBoolean());
        Assert.Equal(3, _graph.Registered.Count);

        // All PIN letters in one PDF, one page per person.
        var pdf = await operatorClient.GetAsync($"/api/batches/{batchId}/pin-letters");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        using (var doc = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import))
            Assert.Equal(3, doc.PageCount);

        // Letters belong to the admin who registered.
        var owner = await _owner.GetAsync($"/api/batches/{batchId}/pin-letters");
        Assert.Equal(HttpStatusCode.Gone, owner.StatusCode);

        // The batch shows up in the tenant's list; auditor has no access.
        var all = await Json(await operatorClient.GetAsync($"/api/tenants/{tenantId}/batches"));
        Assert.Equal("Vertrieb Rollout", Assert.Single(all.EnumerateArray()).GetProperty("name").GetString());
        var auditor = await _app.LoginAsync("audit@home.example", PlatformRole.Auditor);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync($"/api/batches/{batchId}")).StatusCode);

        var verify = await Json(await _owner.GetAsync("/api/audit/verify"));
        Assert.True(verify.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Wrong_key_for_fixed_person_and_skip_flow()
    {
        var tenantId = await OnboardTenantAsync();
        var device = new SimulatedKeyDevice { SerialNumber = 40_000_001 };
        var (agent, agentId) = await PairAgentAsync(_owner, device);
        await using var _ = agent;
        var profiles = await Json(await _owner.GetAsync($"/api/profiles?tenantId={tenantId}"));
        var profileId = profiles.EnumerateArray().First(p => p.GetProperty("isDefault").GetBoolean()).GetProperty("id").GetGuid();
        var candidates = (await Json(await _owner.PostAsJsonAsync($"/api/tenants/{tenantId}/batches/candidates",
            new { csv = "upn;serial\nanna@contoso.com;40000099\n" }))).GetProperty("candidates").EnumerateArray().ToList();
        var batch = await Json(await _owner.PostAsJsonAsync($"/api/tenants/{tenantId}/batches",
            new { defaultProfileId = profileId, source = "CSV", items = candidates.Select(Input) }));
        var batchId = batch.GetProperty("id").GetGuid();
        var anna = batch.GetProperty("items")[0].GetProperty("id").GetGuid();

        // Only Anna is open and she has a fixed key: another key matches nobody.
        var next = await Json(await _owner.PostAsJsonAsync($"/api/batches/{batchId}/next", new { agentId }));
        Assert.Equal("no_match", next.GetProperty("state").GetString());
        var forced = await _owner.PostAsJsonAsync($"/api/batches/{batchId}/next", new { agentId, itemId = anna });
        Assert.Equal(HttpStatusCode.Conflict, forced.StatusCode);
        var register = await _owner.PostAsJsonAsync($"/api/batches/{batchId}/items/{anna}/register", new { agentId, performReset = false, expectedSerialNumber = 40_000_001 });
        Assert.Equal(HttpStatusCode.Conflict, register.StatusCode);

        // Skipping the last open person completes the batch; requeue reopens it.
        var skipped = await Json(await _owner.PostAsync($"/api/batches/{batchId}/items/{anna}/skip", null));
        Assert.Equal("Completed", skipped.GetProperty("status").GetString());
        var requeued = await Json(await _owner.PostAsync($"/api/batches/{batchId}/items/{anna}/requeue", null));
        Assert.Equal("Open", requeued.GetProperty("status").GetString());
        var cancelled = await Json(await _owner.PostAsJsonAsync($"/api/batches/{batchId}/status", new { status = "Cancelled" }));
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _owner.PostAsJsonAsync($"/api/batches/{batchId}/next", new { agentId })).StatusCode);
    }
}
