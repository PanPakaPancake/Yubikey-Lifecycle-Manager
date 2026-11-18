using Microsoft.Extensions.Logging.EventLog;
using Ylm.Agent;
using Ylm.Agent.Core;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "YubikeyLifecycleManagerAgent");
if (OperatingSystem.IsWindows())
{
#pragma warning disable CA1416 // guarded by OperatingSystem.IsWindows()
    builder.Services.Configure<EventLogSettings>(s => { s.SourceName = "Yubikey Lifecycle Manager Agent"; s.LogName = "Application"; });
#pragma warning restore CA1416
}

var simulate = args.Contains("--simulate") || builder.Configuration.GetValue<bool>("Agent:Simulate");
var dataDir = builder.Configuration["Agent:DataDirectory"]
              ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YubikeyLifecycleManager", "Agent");
Directory.CreateDirectory(dataDir);
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(dataDir, "logs")));

builder.Services.AddSingleton<ISecretProtector>(_ => OperatingSystem.IsWindows() ? new DpapiSecretProtector() : new PlainSecretProtector());
builder.Services.AddSingleton(sp => new AgentSettingsStore(Path.Combine(dataDir, "agent.json"), sp.GetRequiredService<ISecretProtector>()));
if (simulate) builder.Services.AddSingleton<IKeyDevice, SimulatedKeyDevice>();
else builder.Services.AddSingleton<IKeyDevice, YubiKeyHardware>();
var agentVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
builder.Services.AddSingleton(sp => new AgentConnection(
    sp.GetRequiredService<IKeyDevice>(),
    sp.GetRequiredService<AgentSettingsStore>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Ylm.Agent.Connection"),
    agentVersion));
builder.Services.AddHostedService(sp => new AgentUpdater(
    sp.GetRequiredService<AgentConnection>(), sp.GetRequiredService<AgentSettingsStore>(), sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<ILogger<AgentUpdater>>(), agentVersion));
builder.Services.AddHostedService<AgentWorker>();
builder.Services.AddHostedService<TrayPipeServer>();

var host = builder.Build();
host.Run();
