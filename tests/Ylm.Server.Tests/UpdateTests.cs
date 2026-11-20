using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Ylm.Core;
using Ylm.Server.Updates;

namespace Ylm.Server.Tests;

public sealed class UpdateTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private readonly string _agentDir = Path.Combine(Path.GetTempPath(), "ylm-agentpkg-" + Guid.NewGuid().ToString("N")[..8]);
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_agentDir);
        File.WriteAllBytes(Path.Combine(_agentDir, UpdateService.AgentFileName), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(_agentDir, "version.txt"), "v9.1.0\n");
        _factory = _app.WithWebHostBuilder(b =>
        {
            b.UseSetting("Ylm:AgentPackageDirectory", _agentDir);
            b.UseSetting("Ylm:UpdateCheckEnabled", "false");
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        _app.Dispose();
        Directory.Delete(_agentDir, true);
        return Task.CompletedTask;
    }

    private HttpClient Client()
    {
        var c = _factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://ylm.test") });
        c.DefaultRequestHeaders.Add("X-YLM-CSRF", "1");
        return c;
    }

    private async Task<HttpClient> LoginAsync(string upn, PlatformRole role)
    {
        var c = Client();
        (await c.PostAsJsonAsync("/auth/dev-login", new { upn, role = role.ToString() })).EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    [Theory]
    [InlineData("v1.2.0", "1.1.9", true)]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.10.0", "1.9.3", true)]
    [InlineData("0.2.0", "0.1.0-dev", true)]
    [InlineData("0.1.0", "0.1.0-dev", false)]
    [InlineData("kaputt", "1.0.0", false)]
    [InlineData("1.0.0", null, false)]
    public void Version_comparison(string candidate, string? current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(candidate, current));

    [Fact]
    public async Task Every_signed_in_user_sees_the_version_summary_but_only_admins_may_apply()
    {
        var auditor = await LoginAsync("auditor@home.example", PlatformRole.Auditor);
        var s = await Json(await auditor.GetAsync("/api/updates/summary"));
        Assert.Equal(UpdateService.CurrentVersion, s.GetProperty("currentVersion").GetString());
        Assert.False(s.GetProperty("updateAvailable").GetBoolean());
        Assert.False(s.GetProperty("canApply").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/api/platform/updates")).StatusCode);

        var owner = await LoginAsync("owner@home.example", PlatformRole.Owner);
        Assert.True((await Json(await owner.GetAsync("/api/updates/summary"))).GetProperty("canApply").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().GetAsync("/api/updates/summary")).StatusCode);
    }

    [Fact]
    public async Task Owner_sees_versions_and_admins_can_download_the_bundled_agent()
    {
        var owner = await LoginAsync("owner@home.example", PlatformRole.Owner);
        var info = await Json(await owner.GetAsync("/api/platform/updates"));
        Assert.Equal(UpdateService.CurrentVersion, info.GetProperty("currentVersion").GetString());
        Assert.False(info.GetProperty("updateAvailable").GetBoolean());
        Assert.Equal("9.1.0", info.GetProperty("agent").GetProperty("bundledVersion").GetString());
        Assert.False(info.GetProperty("updater").GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync("/api/platform/updates/apply", new { version = "9.1.0" })).StatusCode);

        var op = await LoginAsync("op@home.example", PlatformRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/platform/updates")).StatusCode);
        var pkg = await Json(await op.GetAsync("/api/agent-package"));
        Assert.Equal("/api/agent-package/download", pkg.GetProperty("downloadUrl").GetString());
        var file = await op.GetAsync("/api/agent-package/download");
        Assert.Equal([1, 2, 3, 4], await file.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().GetAsync("/api/agent-package/download")).StatusCode);
    }

    [Fact]
    public async Task Agent_gets_update_offer_only_when_bundled_version_is_newer()
    {
        var op = await LoginAsync("op@home.example", PlatformRole.Operator);
        var code = (await Json(await op.PostAsync("/api/agents/pairing-codes", null))).GetProperty("code").GetString();
        var paired = await Json(await Client().PostAsJsonAsync("/api/agent/pair", new { code, machineName = "PC-1", agentVersion = "9.0.0" }));
        var agent = _factory.CreateClient(new() { BaseAddress = new Uri("https://ylm.test") });
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", paired.GetProperty("agentToken").GetString());

        var offer = await Json(await agent.GetAsync("/api/agent/update?current=9.0.0"));
        Assert.Equal("9.1.0", offer.GetProperty("version").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([1, 2, 3, 4])), offer.GetProperty("sha256").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await agent.GetAsync("/api/agent/update?current=9.1.0")).StatusCode);
        Assert.Equal([1, 2, 3, 4], await agent.GetByteArrayAsync(offer.GetProperty("url").GetString()));

        var anonymous = _factory.CreateClient(new() { BaseAddress = new Uri("https://ylm.test") });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/agent/update?current=1.0.0")).StatusCode);
    }

    [Fact]
    public async Task Agent_update_info_carries_server_policy_and_never_blocks_old_agents()
    {
        var owner = await LoginAsync("owner@home.example", PlatformRole.Owner);
        var code = (await Json(await owner.PostAsync("/api/agents/pairing-codes", null))).GetProperty("code").GetString();
        var paired = await Json(await Client().PostAsJsonAsync("/api/agent/pair", new { code, machineName = "PC-2", agentVersion = "9.0.0" }));
        var agent = _factory.CreateClient(new() { BaseAddress = new Uri("https://ylm.test") });
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", paired.GetProperty("agentToken").GetString());

        var info = await Json(await agent.GetAsync("/api/agent/update?info=1&current=9.0.0"));
        Assert.True(info.GetProperty("available").GetBoolean());
        Assert.Equal("9.1.0", info.GetProperty("version").GetString());
        Assert.Equal("AgentDecides", info.GetProperty("policy").GetString());
        var current = await Json(await agent.GetAsync("/api/agent/update?info=1&current=9.1.0"));
        Assert.False(current.GetProperty("available").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await (await LoginAsync("op@home.example", PlatformRole.Operator))
            .PutAsJsonAsync("/api/platform/updates/agent-policy", new { policy = "Never" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/platform/updates/agent-policy", new { policy = "Never" })).StatusCode);
        info = await Json(await agent.GetAsync("/api/agent/update?info=1&current=9.0.0"));
        Assert.Equal("Never", info.GetProperty("policy").GetString());
        Assert.True(info.GetProperty("available").GetBoolean());
        // Ältere Agents ohne info=1 installieren jedes Angebot sofort: bei "Nie" bekommen sie keins.
        Assert.Equal(HttpStatusCode.NoContent, (await agent.GetAsync("/api/agent/update?current=9.0.0")).StatusCode);

        var overview = (await Json(await owner.GetAsync("/api/platform/updates"))).GetProperty("agent");
        Assert.Equal("Never", overview.GetProperty("policy").GetString());
        Assert.Contains(overview.GetProperty("list").EnumerateArray(), a => a.GetProperty("machineName").GetString() == "PC-2" && a.GetProperty("outdated").GetBoolean());
    }
}
