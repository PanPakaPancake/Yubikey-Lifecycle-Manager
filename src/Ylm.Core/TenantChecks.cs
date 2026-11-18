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

/// <summary>An Entra group referenced by the platform (id plus the name shown in the UI).</summary>
public sealed record GroupRef(string Id, string Name);

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

    /// <param name="targetGroups">Inclusion groups of the tenant's target audience; empty or null = all users.
    /// Exclusion groups are deliberately not checked: "does not need a key" is not the same as "must not register one".</param>
    public static IReadOnlyList<TenantCheckResult> Analyze(Fido2Policy policy, IReadOnlyCollection<Guid> yubiKeyAaguids, IReadOnlyList<GroupRef>? targetGroups)
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

        results.Add(CheckTarget(policy, targetGroups));
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

    private static IReadOnlyList<GroupRef> Wanted(IReadOnlyList<GroupRef>? targetGroups) =>
        targetGroups is { Count: > 0 } ? targetGroups : [new GroupRef(PolicyTarget.AllUsers, "Alle Benutzer")];

    private static TenantCheckResult CheckTarget(Fido2Policy policy, IReadOnlyList<GroupRef>? targetGroups)
    {
        const string title = "Zielgruppe für die Methode freigeschaltet";
        var wanted = Wanted(targetGroups);
        var allUsersIncluded = policy.IncludeTargets.Any(t => t.Id == PolicyTarget.AllUsers);
        var excluded = wanted.Where(g => policy.ExcludeTargets.Any(t => t.Id == g.Id)).ToList();
        var notIncluded = wanted.Where(g => !allUsersIncluded && policy.IncludeTargets.All(t => t.Id != g.Id)).ToList();
        static string Names(IEnumerable<GroupRef> groups) => string.Join(", ", groups.Select(g => $"„{g.Name}“"));

        var manual = $"{PolicyPath} → Aktivieren und als Ziel festlegen: " +
                     (notIncluded.Count > 0 ? $"unter „Einschließen“ {Names(notIncluded)} hinzufügen" : "") +
                     (notIncluded.Count > 0 && excluded.Count > 0 ? " und " : "") +
                     (excluded.Count > 0 ? $"unter „Ausschließen“ {Names(excluded)} entfernen" : "") + ".";

        if (excluded.Count > 0)
            return new(TenantCheckIds.TargetGroup, title, CheckStatus.Failed,
                (excluded.Count == 1 ? $"Die Gruppe {Names(excluded)} ist" : $"Die Gruppen {Names(excluded)} sind") + " von der Methode ausgeschlossen.", true, manual);
        if (notIncluded.Count > 0)
            return new(TenantCheckIds.TargetGroup, title, CheckStatus.Failed,
                wanted[0].Id == PolicyTarget.AllUsers
                    ? "Die Methode ist nicht für alle Benutzer freigegeben."
                    : (notIncluded.Count == 1 ? $"Die Gruppe {Names(notIncluded)} ist" : $"Die Gruppen {Names(notIncluded)} sind") + " der Methode nicht zugewiesen.",
                true, manual);
        return new(TenantCheckIds.TargetGroup, title, CheckStatus.Ok,
            wanted[0].Id == PolicyTarget.AllUsers ? "Die Methode gilt für alle Benutzer."
            : allUsersIncluded ? "Die Methode gilt für alle Benutzer und damit auch für die Zielgruppe."
            : wanted.Count == 1 ? "Die Zielgruppe ist freigeschaltet." : $"Alle {wanted.Count} Gruppen der Zielgruppe sind freigeschaltet.", false, "");
    }

    /// <summary>Returns the policy with all fixable findings corrected ("Jetzt beheben").</summary>
    public static Fido2Policy Fix(Fido2Policy policy, IReadOnlyCollection<Guid> yubi, IReadOnlyList<GroupRef>? targetGroups, IReadOnlyCollection<string> checkIds)
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
            var wanted = Wanted(targetGroups).Select(g => g.Id).ToList();
            var include = fixedPolicy.IncludeTargets.ToList();
            if (include.All(t => t.Id != PolicyTarget.AllUsers))
                include.AddRange(wanted.Where(id => include.All(t => t.Id != id)).Select(id => new PolicyTarget(id)));
            fixedPolicy = fixedPolicy with
            {
                IncludeTargets = include,
                ExcludeTargets = fixedPolicy.ExcludeTargets.Where(t => !wanted.Contains(t.Id)).ToList(),
            };
        }
        return fixedPolicy;
    }
}
