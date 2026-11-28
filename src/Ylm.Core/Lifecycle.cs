using Ylm.Contracts.Localization;

namespace Ylm.Core;

/// <summary>Im Lager → Zugewiesen/Aktiv → Gesperrt → Ausgemustert (planning 5.2).</summary>
public enum KeyStatus
{
    InStock = 0,
    Active = 1,
    Locked = 2,
    Retired = 3,
}

public enum LockReason
{
    Lost = 0,
    Defective = 1,
    Stolen = 2,
    Other = 3,
}

public enum KeySource
{
    /// <summary>Registered through the platform, full data incl. serial/firmware.</summary>
    Platform = 0,
    /// <summary>Taken over from Entra by sync, no serial/firmware.</summary>
    EntraSync = 1,
    /// <summary>Stock intake by plugging the key in.</summary>
    StockIntake = 2,
    /// <summary>Stock intake by CSV import.</summary>
    Import = 3,
}

public static class KeyLifecycle
{
    private static readonly Dictionary<KeyStatus, KeyStatus[]> Allowed = new()
    {
        [KeyStatus.InStock] = [KeyStatus.Active, KeyStatus.Retired],
        // Active → InStock happens through a factory reset (credentials are wiped).
        [KeyStatus.Active] = [KeyStatus.Locked, KeyStatus.InStock],
        // A locked key has no Entra registrations left; it can be reset back to stock or retired.
        [KeyStatus.Locked] = [KeyStatus.InStock, KeyStatus.Retired],
        [KeyStatus.Retired] = [],
    };

    public static bool CanTransition(KeyStatus from, KeyStatus to) => from != to && Allowed[from].Contains(to);

    public static void EnsureTransition(KeyStatus from, KeyStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainException("invalid_transition",
                L.F($"Statuswechsel von „{DisplayName(from)}“ nach „{DisplayName(to)}“ ist nicht erlaubt."));
        }
    }

    public static IReadOnlyList<KeyStatus> NextStates(KeyStatus from) => Allowed[from];

    public static string DisplayName(KeyStatus status) => status switch
    {
        KeyStatus.InStock => L.T("Im Lager"),
        KeyStatus.Active => L.T("Zugewiesen/Aktiv"),
        KeyStatus.Locked => L.T("Gesperrt"),
        KeyStatus.Retired => L.T("Ausgemustert"),
        _ => status.ToString(),
    };

    public static string DisplayName(LockReason reason) => reason switch
    {
        LockReason.Lost => L.T("Verloren"),
        LockReason.Defective => L.T("Defekt"),
        LockReason.Stolen => L.T("Gestohlen"),
        _ => L.T("Sonstiges"),
    };
}

public sealed class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
