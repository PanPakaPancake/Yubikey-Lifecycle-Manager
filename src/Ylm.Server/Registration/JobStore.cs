using System.Collections.Concurrent;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Registration;

public enum JobState { Running, Succeeded, Failed }

/// <summary>A long running operation with the agent (registration, reset) the browser polls for progress.</summary>
public sealed class OperationJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid UserId { get; init; }
    /// <summary>Null bei Keys im Partnerlager.</summary>
    public required Guid? TenantId { get; init; }
    public required string Kind { get; init; }
    public JobState State { get; set; } = JobState.Running;
    public string Step { get; set; } = L.T("Wird gestartet …");
    public string? Prompt { get; set; }
    public int StepNumber { get; set; }
    public int TotalSteps { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public Guid? KeyId { get; set; }
    public Guid? PinLetterId { get; set; }
    public int? SerialNumber { get; set; }
    public bool? PinChangeForced { get; set; }
    /// <summary>Set when the job registers one person of a Stapelregistrierung.</summary>
    public Guid? BatchItemId { get; set; }
    public List<string> Notes { get; } = [];
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Agent running the job; its live prompt overrides the step prompt while the job runs.</summary>
    public Agents.ConnectedAgent? Agent { get; set; }

    public object ToDto() => new
    {
        Id, Kind, State = State.ToString(), Step, BatchItemId, SerialNumber, Prompt = State == JobState.Running ? Agent?.CurrentPrompt ?? Prompt : Prompt, StepNumber, TotalSteps, Error, ErrorCode, KeyId, PinLetterId, Notes, CreatedAt, FinishedAt,
    };
}

public sealed class JobStore
{
    private readonly ConcurrentDictionary<Guid, OperationJob> _jobs = new();

    public OperationJob Add(OperationJob job)
    {
        Cleanup();
        _jobs[job.Id] = job;
        return job;
    }

    public bool Exists(Guid id) => _jobs.ContainsKey(id);

    public OperationJob? Get(Guid id, Guid userId) => _jobs.TryGetValue(id, out var j) && j.UserId == userId ? j : null;

    private void Cleanup()
    {
        foreach (var (id, job) in _jobs)
        {
            if (job.FinishedAt is { } f && f < DateTimeOffset.UtcNow.AddHours(-2)) _jobs.TryRemove(id, out _);
        }
    }
}

/// <summary>
/// Holds generated PIN letters in memory only, for a short time. PINs are never written to the database
/// (planning 6.2). After expiry or server restart the PDF is gone.
/// </summary>
public sealed class PinLetterStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    /// <summary>Letters of a Stapelregistrierung stay longer, so they can be printed together at the end.</summary>
    public static readonly TimeSpan BatchLifetime = TimeSpan.FromHours(4);
    private readonly ConcurrentDictionary<Guid, (Guid UserId, byte[] Pdf, string FileName, DateTimeOffset Expires)> _letters = new();

    public Guid Add(Guid userId, byte[] pdf, string fileName, TimeSpan? lifetime = null)
    {
        foreach (var (key, v) in _letters)
        {
            if (v.Expires < DateTimeOffset.UtcNow) _letters.TryRemove(key, out _);
        }
        var id = Guid.NewGuid();
        _letters[id] = (userId, pdf, fileName, DateTimeOffset.UtcNow.Add(lifetime ?? Lifetime));
        return id;
    }

    public (byte[] Pdf, string FileName)? Get(Guid id, Guid userId) =>
        _letters.TryGetValue(id, out var v) && v.UserId == userId && v.Expires > DateTimeOffset.UtcNow ? (v.Pdf, v.FileName) : null;

    public void Remove(Guid id) => _letters.TryRemove(id, out _);
}
