using System.Collections.Concurrent;

namespace Ylm.Server.Registration;

public enum JobState { Running, Succeeded, Failed }

/// <summary>A long running operation with the agent (registration, reset) the browser polls for progress.</summary>
public sealed class OperationJob
{
    public Guid Id { get; } = Guid.NewGuid();
    public required Guid UserId { get; init; }
    public required Guid TenantId { get; init; }
    public required string Kind { get; init; }
    public JobState State { get; set; } = JobState.Running;
    public string Step { get; set; } = "Wird gestartet …";
    public string? Prompt { get; set; }
    public int StepNumber { get; set; }
    public int TotalSteps { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public Guid? KeyId { get; set; }
    public Guid? PinLetterId { get; set; }
    public List<string> Notes { get; } = [];
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Agent running the job; its live prompt overrides the step prompt while the job runs.</summary>
    public Agents.ConnectedAgent? Agent { get; set; }

    public object ToDto() => new
    {
        Id, Kind, State = State.ToString(), Step, Prompt = State == JobState.Running ? Agent?.CurrentPrompt ?? Prompt : Prompt, StepNumber, TotalSteps, Error, ErrorCode, KeyId, PinLetterId, Notes, CreatedAt, FinishedAt,
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
    private readonly ConcurrentDictionary<Guid, (Guid UserId, byte[] Pdf, string FileName, DateTimeOffset Expires)> _letters = new();

    public Guid Add(Guid userId, byte[] pdf, string fileName)
    {
        foreach (var (key, v) in _letters)
        {
            if (v.Expires < DateTimeOffset.UtcNow) _letters.TryRemove(key, out _);
        }
        var id = Guid.NewGuid();
        _letters[id] = (userId, pdf, fileName, DateTimeOffset.UtcNow.Add(Lifetime));
        return id;
    }

    public (byte[] Pdf, string FileName)? Get(Guid id, Guid userId) =>
        _letters.TryGetValue(id, out var v) && v.UserId == userId && v.Expires > DateTimeOffset.UtcNow ? (v.Pdf, v.FileName) : null;

    public void Remove(Guid id) => _letters.TryRemove(id, out _);
}
