namespace Ylm.Core;

/// <summary>Relevant parts of the Entra FIDO2 authentication method configuration.</summary>
public sealed record Fido2Policy
{
    public bool Enabled { get; init; }
    public bool IsSelfServiceRegistrationAllowed { get; init; } = true;
    public bool IsAttestationEnforced { get; init; }
    public bool KeyRestrictionsEnforced { get; init; }
    /// <summary>"allow" or "block".</summary>
    public string EnforcementType { get; init; } = "allow";
    public IReadOnlyList<Guid> AaGuids { get; init; } = [];
    public IReadOnlyList<PolicyTarget> IncludeTargets { get; init; } = [];
    public IReadOnlyList<PolicyTarget> ExcludeTargets { get; init; } = [];
}

public sealed record PolicyTarget(string Id, string TargetType = "group", bool IsRegistrationRequired = false)
{
    public const string AllUsers = "all_users";
}

public enum CheckStatus { Ok, Warning, Failed }

public sealed record TenantCheckResult(string Id, string Title, CheckStatus Status, string Message, bool CanFix, string ManualInstructions);

public static class TenantCheckIds
{
    public const string Fido2Enabled = "fido2-enabled";
    public const string AaguidsAllowed = "yubikey-aaguids-allowed";
    public const string TargetGroup = "target-group";
    public const string GraphPermissions = "graph-permissions";
}

/// <summary>Evaluates the tenant's FIDO2 policy (planning 4.1 step 3) and computes the fix.</summary>
public static class Fido2PolicyAnalyzer
{
    private const string PolicyPath = "Entra Admin Center → Schutz → Authentifizierungsmethoden → Richtlinien → Passkey (FIDO2)";

    public static IReadOnlyList<TenantCheckResult> Analyze(Fido2Policy policy, IReadOnlyCollection<Guid> yubiKeyAaguids, string? targetGroupId)
    {
        var results = new List<TenantCheckResult>
        {
            policy.Enabled
                ? new(TenantCheckIds.Fido2Enabled, "Passkey-/FIDO2-Methode aktiviert", CheckStatus.Ok, "Die Methode ist aktiviert.", false, "")
                : new(TenantCheckIds.Fido2Enabled, "Passkey-/FIDO2-Methode aktiviert", CheckStatus.Failed,
                    "Die Passkey-/FIDO2-Methode ist im Tenant deaktiviert.", true,
                    $"{PolicyPath}: Schalter „Aktivieren“ auf „Ein“ stellen und speichern."),
        };

        var (aaguidOk, aaguidMsg) = CheckAaguids(policy, yubiKeyAaguids);
        results.Add(aaguidOk
            ? new(TenantCheckIds.AaguidsAllowed, "YubiKey-AAGUIDs erlaubt", CheckStatus.Ok, aaguidMsg, false, "")
            : new(TenantCheckIds.AaguidsAllowed, "YubiKey-AAGUIDs erlaubt", CheckStatus.Failed, aaguidMsg, true,
                $"{PolicyPath} → Konfigurieren → Schlüsseleinschränkungen: " +
                (policy.EnforcementType == "block"
                    ? "die YubiKey-AAGUIDs aus der Sperrliste entfernen."
                    : "die fehlenden YubiKey-AAGUIDs zur Zulassungsliste hinzufügen: " + string.Join(", ", MissingAllowed(policy, yubiKeyAaguids)))));

        results.Add(CheckTarget(policy, targetGroupId));
        return results;
    }

    private static IEnumerable<Guid> MissingAllowed(Fido2Policy policy, IEnumerable<Guid> yubi) =>
        yubi.Where(g => !policy.AaGuids.Contains(g));

    private static (bool, string) CheckAaguids(Fido2Policy policy, IReadOnlyCollection<Guid> yubi)
    {
        if (!policy.KeyRestrictionsEnforced) return (true, "Es sind keine Schlüsseleinschränkungen aktiv, alle YubiKeys sind erlaubt.");
        if (policy.EnforcementType == "block")
        {
            var blocked = yubi.Where(g => policy.AaGuids.Contains(g)).ToList();
            return blocked.Count == 0
                ? (true, "Die Sperrliste enthält keine YubiKey-AAGUIDs.")
                : (false, $"{blocked.Count} YubiKey-AAGUID(s) stehen auf der Sperrliste.");
        }
        var missing = MissingAllowed(policy, yubi).ToList();
        return missing.Count == 0
            ? (true, "Alle bekannten YubiKey-AAGUIDs stehen auf der Zulassungsliste.")
            : (false, $"{missing.Count} von {yubi.Count} YubiKey-AAGUIDs fehlen auf der Zulassungsliste.");
    }

    private static TenantCheckResult CheckTarget(Fido2Policy policy, string? targetGroupId)
    {
        const string title = "Zielgruppe für die Methode freigeschaltet";
        var wanted = string.IsNullOrWhiteSpace(targetGroupId) ? PolicyTarget.AllUsers : targetGroupId;
        var manual = $"{PolicyPath} → Aktivieren und als Ziel festlegen: Gruppe „{(wanted == PolicyTarget.AllUsers ? "Alle Benutzer" : wanted)}“ hinzufügen und bei „Ausschließen“ entfernen.";
        if (policy.ExcludeTargets.Any(t => t.Id == wanted))
            return new(TenantCheckIds.TargetGroup, title, CheckStatus.Failed, "Die Zielgruppe ist von der Methode ausgeschlossen.", true, manual);
        var included = policy.IncludeTargets.Any(t => t.Id == wanted || t.Id == PolicyTarget.AllUsers);
        return included
            ? new(TenantCheckIds.TargetGroup, title, CheckStatus.Ok,
                wanted == PolicyTarget.AllUsers ? "Die Methode gilt für alle Benutzer." : "Die Zielgruppe ist freigeschaltet.", false, "")
            : new(TenantCheckIds.TargetGroup, title, CheckStatus.Failed,
                wanted == PolicyTarget.AllUsers ? "Die Methode ist keiner Gruppe zugewiesen." : "Die Zielgruppe ist der Methode nicht zugewiesen.", true, manual);
    }

    /// <summary>Returns the policy with all fixable findings corrected ("Jetzt beheben").</summary>
    public static Fido2Policy Fix(Fido2Policy policy, IReadOnlyCollection<Guid> yubi, string? targetGroupId, IReadOnlyCollection<string> checkIds)
    {
        var fixedPolicy = policy;
        if (checkIds.Contains(TenantCheckIds.Fido2Enabled)) fixedPolicy = fixedPolicy with { Enabled = true };
        if (checkIds.Contains(TenantCheckIds.AaguidsAllowed) && fixedPolicy.KeyRestrictionsEnforced)
        {
            fixedPolicy = fixedPolicy.EnforcementType == "block"
                ? fixedPolicy with { AaGuids = fixedPolicy.AaGuids.Where(g => !yubi.Contains(g)).ToList() }
                : fixedPolicy with { AaGuids = fixedPolicy.AaGuids.Concat(yubi).Distinct().ToList() };
        }
        if (checkIds.Contains(TenantCheckIds.TargetGroup))
        {
            var wanted = string.IsNullOrWhiteSpace(targetGroupId) ? PolicyTarget.AllUsers : targetGroupId;
            var include = fixedPolicy.IncludeTargets.Any(t => t.Id == wanted || t.Id == PolicyTarget.AllUsers)
                ? fixedPolicy.IncludeTargets
                : [.. fixedPolicy.IncludeTargets, new PolicyTarget(wanted)];
            fixedPolicy = fixedPolicy with
            {
                IncludeTargets = include,
                ExcludeTargets = fixedPolicy.ExcludeTargets.Where(t => t.Id != wanted).ToList(),
            };
        }
        return fixedPolicy;
    }
}
