using Ylm.Agent.Core;

namespace Ylm.Agent;

public sealed class AgentWorker(AgentConnection connection, ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Agent startet (Status: {State})", connection.Status.Connection);
        await connection.StartAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await connection.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}
