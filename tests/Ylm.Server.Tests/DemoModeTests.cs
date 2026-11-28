using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Demo;

namespace Ylm.Server.Tests;

/// <summary>Server im Demo-Modus (docker-compose.demo.yml) mit eigener Datenbank und eigenem Datenordner.</summary>
public sealed class DemoApp(string database, string dataDir, bool demoMode = true) : WebApplicationFactory<Program>
{
    public string ConnectionString => $"{TestApp.BaseConnection};Database={database}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
        builder.UseSetting("Ylm:DemoMode", demoMode ? "true" : "false");
        builder.UseSetting("Ylm:EnableBackgroundJobs", "false");
        builder.UseSetting("Ylm:UpdateCheckEnabled", "false");
        builder.UseSetting("Ylm:DataDirectory", dataDir);
        builder.UseSetting("Ylm:PublicBaseUrl", "http://localhost:8099");
    }

    public HttpClient Browser()
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("http://localhost:8099") });
        c.DefaultRequestHeaders.Add("X-YLM-CSRF", "1");
        return c;
    }

    public async Task WithDbAsync(Func<YlmDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<YlmDbContext>());
    }
}

public sealed class DemoModeTests : IDisposable
{
    private readonly string _database = "ylm_demo_" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ylm-demo-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            NpgsqlConnection.ClearAllPools();
            using var conn = new NpgsqlConnection(TestApp.BaseConnection + ";Database=postgres");
            conn.Open();
            using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", conn);
            cmd.ExecuteNonQuery();
            Directory.Delete(_dataDir, true);
        }
        catch (Exception) { /* best effort */ }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Demo_starts_with_sample_data_and_role_login()
    {
        using var app = new DemoApp(_database, _dataDir);
        var c = app.Browser();

        var status = await Json(await c.GetAsync("/api/setup/status"));
        Assert.True(status.GetProperty("setupComplete").GetBoolean());
        Assert.True(status.GetProperty("demo").GetBoolean());
        Assert.True((await Json(await c.GetAsync("/auth/methods"))).GetProperty("demo").GetBoolean());
        Assert.Equal(4, (await Json(await c.GetAsync("/auth/demo"))).GetArrayLength());

        // Ohne Anmeldung nichts, nach Rollenwahl angemeldet mit genau dieser Rolle.
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/tenants")).StatusCode);
        await Json(await c.PostAsJsonAsync("/auth/demo-login", new { role = "Auditor" }));
        var me = await Json(await c.GetAsync("/api/me"));
        Assert.Equal("Auditor", me.GetProperty("user").GetProperty("role").GetString());

        var tenants = await Json(await c.GetAsync("/api/tenants"));
        Assert.Equal(2, tenants.GetArrayLength());
        Assert.All(tenants.EnumerateArray(), t => Assert.True(t.GetProperty("lastSyncSucceeded").GetBoolean()));

        await app.WithDbAsync(async db =>
        {
            Assert.True(await db.Keys.CountAsync(k => k.Status == KeyStatus.Active) > 30);
            Assert.True(await db.Keys.AnyAsync(k => k.Status == KeyStatus.InStock && k.TenantId == null), "Partnerlager");
            Assert.True(await db.Keys.AnyAsync(k => k.Source == KeySource.EntraSync), "selbst registrierte Keys aus dem Sync");
            Assert.True(await db.Keys.AnyAsync(k => k.Usage == KeyUsage.Breakglass && k.Registrations.Count == 2));
            Assert.True(await db.Keys.AnyAsync(k => k.Usage == KeyUsage.Shared && k.Custodian != null));
            Assert.True(await db.Todos.AnyAsync(t => t.Status == TodoStatus.Open));
            // Contoso zeigt eine fehlgeschlagene Prüfung mit „Jetzt beheben“.
            var contoso = await db.Tenants.SingleAsync(t => t.EntraTenantId == DemoData.ContosoTenantId);
            Assert.Contains(TenantCheckIds.AaguidsAllowed, contoso.LastChecksJson);
        });
        Assert.True(File.Exists(Path.Combine(_dataDir, DemoMode.MarkerFile)));
    }

    [Fact]
    public async Task Restart_keeps_data_without_duplicates_and_sync_finds_every_key_again()
    {
        int keys;
        using (var first = new DemoApp(_database, _dataDir))
        {
            first.Browser();
            keys = 0;
            await first.WithDbAsync(async db => keys = await db.Keys.CountAsync());
        }

        using var second = new DemoApp(_database, _dataDir);
        var c = second.Browser();
        await Json(await c.PostAsJsonAsync("/auth/demo-login", new { role = "Owner" }));
        var tenants = await Json(await c.GetAsync("/api/tenants"));
        foreach (var t in tenants.EnumerateArray())
        {
            var result = await Json(await c.PostAsync($"/api/tenants/{t.GetProperty("id").GetString()}/sync", null));
            Assert.Equal(0, result.GetProperty("removed").GetInt32());
            Assert.Equal(0, result.GetProperty("imported").GetInt32());
        }
        await second.WithDbAsync(async db => Assert.Equal(keys, await db.Keys.CountAsync()));
    }

    [Fact]
    public async Task Demo_refuses_a_database_of_a_real_installation()
    {
        using (var real = new DemoApp(_database, _dataDir, demoMode: false))
        {
            real.Browser();
            await real.WithDbAsync(async db =>
            {
                var s = await db.Installation.FirstAsync();
                s.SetupCompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            });
        }

        using var demo = new DemoApp(_database, _dataDir);
        var ex = Assert.ThrowsAny<Exception>(() => demo.Browser());
        Assert.Contains("Demo-Modus abgebrochen", ex.ToString());
    }

    [Fact]
    public void Demo_data_folder_cannot_be_reused_for_real_operation()
    {
        using (var demo = new DemoApp(_database, _dataDir)) demo.Browser();

        using var real = new DemoApp(_database, _dataDir, demoMode: false);
        var ex = Assert.ThrowsAny<Exception>(() => real.Browser());
        Assert.Contains("gehört zur Demo", ex.ToString());
    }

    [Fact]
    public async Task Without_demo_mode_there_is_no_demo_login()
    {
        using var app = new TestApp();
        var c = app.Client();
        Assert.False((await c.PostAsJsonAsync("/auth/demo-login", new { role = "Owner" })).IsSuccessStatusCode);
        Assert.False((await Json(await c.GetAsync("/auth/methods"))).GetProperty("demo").GetBoolean());
    }
}
