using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ylm.Agent.Core;
using Ylm.Server.Data;
using Ylm.Server.Graph;

namespace Ylm.Server.Tests;

/// <summary>Server with a fresh PostgreSQL database, fake Graph and dev login.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public static string BaseConnection => Environment.GetEnvironmentVariable("YLM_TEST_POSTGRES")
                                           ?? "Host=localhost;Username=ylm;Password=ylm";

    private readonly string _database = "ylm_test_" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ylm-test-" + Guid.NewGuid().ToString("N")[..8]);
    public FakeGraphFactory Graph { get; } = new();
    public FakeEntraBootstrap Bootstrap { get; } = new();
    public string ConnectionString => $"{BaseConnection};Database={_database}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", ConnectionString);
        builder.UseSetting("Ylm:AuthMode", "Development");
        builder.UseSetting("Ylm:EnableBackgroundJobs", "false");
        builder.UseSetting("Ylm:DataDirectory", _dataDir);
        builder.UseSetting("Ylm:PublicBaseUrl", "https://ylm.test");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IGraphClientFactory>();
            s.AddSingleton<IGraphClientFactory>(Graph);
            s.RemoveAll<Ylm.Server.Setup.IEntraBootstrap>();
            s.AddSingleton<Ylm.Server.Setup.IEntraBootstrap>(Bootstrap);
        });
    }

    public HttpClient Client()
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://ylm.test") });
        c.DefaultRequestHeaders.Add("X-YLM-CSRF", "1");
        return c;
    }

    public async Task<HttpClient> LoginAsync(string upn, Ylm.Core.PlatformRole? role = null)
    {
        var c = Client();
        var res = await c.PostAsJsonAsync("/auth/dev-login", new { upn, displayName = upn.Split('@')[0], role = role?.ToString() });
        res.EnsureSuccessStatusCode();
        return c;
    }

    public async Task WithDbAsync(Func<YlmDbContext, Task> action)
    {
        using var scope = Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<YlmDbContext>());
    }

    public AgentConnection CreateAgent(IKeyDevice device) => new(
        device,
        new AgentSettingsStore(Path.Combine(_dataDir, "agent-" + Guid.NewGuid().ToString("N")[..6] + ".json"), new PlainSecretProtector()),
        NullLogger.Instance,
        "1.0.0-test",
        () => Server.CreateHandler(),
        o => o.Transports = HttpTransportType.LongPolling);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try
        {
            NpgsqlConnection.ClearAllPools();
            using var conn = new NpgsqlConnection(BaseConnection + ";Database=postgres");
            conn.Open();
            using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", conn);
            cmd.ExecuteNonQuery();
            Directory.Delete(_dataDir, true);
        }
        catch (Exception) { /* best effort */ }
    }
}

internal static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList()) services.Remove(d);
    }
}
