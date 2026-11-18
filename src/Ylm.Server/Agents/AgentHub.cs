using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Ylm.Contracts;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;

namespace Ylm.Server.Agents;

public sealed record ConnectedAgent(Guid AgentId, Guid UserId, string ConnectionId, string MachineName, string? Version, DateTimeOffset ConnectedAt)
{
    public KeyInfo? CurrentKey { get; set; }
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public string? BusyWith { get; set; }
    /// <summary>Live instruction from the agent during an operation (unplug, plug in, touch).</summary>
    public string? CurrentPrompt { get; set; }
}

/// <summary>In-memory registry of connected agents (single server instance; see docs/BETRIEB.md for scale-out).</summary>
public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<Guid, ConnectedAgent> _agents = new();

    public void Connected(ConnectedAgent agent) => _agents[agent.AgentId] = agent;

    public void Disconnected(Guid agentId, string connectionId)
    {
        if (_agents.TryGetValue(agentId, out var a) && a.ConnectionId == connectionId) _agents.TryRemove(agentId, out _);
    }

    public ConnectedAgent? Get(Guid agentId) => _agents.GetValueOrDefault(agentId);

    public IReadOnlyList<ConnectedAgent> ForUser(Guid userId) => _agents.Values.Where(a => a.UserId == userId).ToList();

    public void Disconnect(Guid agentId) => _agents.TryRemove(agentId, out _);
}

[Authorize(AuthenticationSchemes = AuthSchemes.Agent)]
public sealed class AgentHub(AgentRegistry registry, YlmDbContext db, ILogger<AgentHub> logger) : Hub<IAgentClient>
{
    private Guid AgentId => Guid.Parse(Context.User!.FindFirstValue(YlmClaims.AgentId)!);
    private Guid UserId => Guid.Parse(Context.User!.FindFirstValue(YlmClaims.UserId)!);

    public override async Task OnConnectedAsync()
    {
        var agent = await db.Agents.FirstAsync(a => a.Id == AgentId);
        agent.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        registry.Connected(new ConnectedAgent(agent.Id, UserId, Context.ConnectionId, agent.MachineName, agent.AgentVersion, DateTimeOffset.UtcNow));
        logger.LogInformation("Agent {Machine} connected", agent.MachineName);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Disconnected(AgentId, Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task Hello(AgentHello hello)
    {
        if (hello.ProtocolVersion != AgentProtocol.ProtocolVersion)
            throw new HubException($"Protokollversion {hello.ProtocolVersion} wird nicht unterstützt, bitte den Agent aktualisieren.");
        var agent = await db.Agents.FirstAsync(a => a.Id == AgentId);
        agent.AgentVersion = hello.AgentVersion;
        agent.MachineName = hello.MachineName;
        agent.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        if (registry.Get(AgentId) is { } a) registry.Connected(a with { MachineName = hello.MachineName, Version = hello.AgentVersion });
    }

    public Task ReportPrompt(string? text)
    {
        if (registry.Get(AgentId) is { } a) a.CurrentPrompt = text;
        return Task.CompletedTask;
    }

    public Task ReportKeyState(KeyInfo? key)
    {
        if (registry.Get(AgentId) is { } a)
        {
            a.CurrentKey = key;
            a.LastSeen = DateTimeOffset.UtcNow;
        }
        return Task.CompletedTask;
    }
}

/// <summary>Calls into a specific connected agent with timeouts and readable errors.</summary>
public sealed class AgentGateway(IHubContext<AgentHub, IAgentClient> hub, AgentRegistry registry)
{
    public ConnectedAgent Require(Guid agentId, Guid userId)
    {
        var agent = registry.Get(agentId);
        if (agent is null || agent.UserId != userId)
            throw ApiException.Conflict("agent_offline", "Der Agent ist nicht verbunden. Bitte prüfen Sie die Tray-App auf Ihrem PC.");
        return agent;
    }

    public IAgentClient Client(ConnectedAgent agent) => hub.Clients.Client(agent.ConnectionId);

    public async Task<T> CallAsync<T>(ConnectedAgent agent, Func<IAgentClient, CancellationToken, Task<T>> call, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await call(Client(agent), cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw ApiException.Conflict("agent_timeout", "Der Agent hat nicht rechtzeitig geantwortet.");
        }
        catch (IOException ex)
        {
            throw ApiException.Conflict("agent_disconnected", "Die Verbindung zum Agent wurde unterbrochen: " + ex.Message);
        }
        catch (HubException ex)
        {
            throw ApiException.Conflict("agent_error", "Fehler im Agent: " + ex.Message);
        }
    }

    public async Task<KeyInfo?> ReadKeyAsync(ConnectedAgent agent, CancellationToken ct = default)
    {
        var key = await CallAsync(agent, (c, t) => c.GetKeyInfo(t), TimeSpan.FromSeconds(20), ct);
        agent.CurrentKey = key;
        return key;
    }

    public void Progress(ConnectedAgent agent, AgentProgress progress) => _ = Client(agent).ShowProgress(progress);
}
