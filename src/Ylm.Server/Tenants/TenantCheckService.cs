using System.Text.Json;
using Microsoft.Extensions.Options;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Server.Setup;

namespace Ylm.Server.Tenants;

public sealed class TenantCheckService(YlmDbContext db, IGraphClientFactory graphFactory, AuditService audit, IOptions<YlmOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public IReadOnlyCollection<Guid> YubiKeyAaguids =>
        YubicoAaguids.Known.Keys
            .Concat(options.Value.AdditionalYubiKeyAaguids.Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty))
            .Distinct().ToList();

    public static IReadOnlyList<TenantCheckResult> ReadChecks(Tenant t) =>
        t.LastChecksJson is null ? [] : JsonSerializer.Deserialize<List<TenantCheckResult>>(t.LastChecksJson, Json) ?? [];

    public static Fido2Policy? ReadPolicy(Tenant t) =>
        t.PolicySnapshotJson is null ? null : JsonSerializer.Deserialize<Fido2Policy>(t.PolicySnapshotJson, Json);

    /// <param name="freshToken">true when an admin explicitly re-runs the checks: always fetch a new app-only token.</param>
    public async Task<IReadOnlyList<TenantCheckResult>> RunAsync(Tenant tenant, AuditActor actor, CancellationToken ct = default, bool freshToken = false)
    {
        if (freshToken) graphFactory.Invalidate(tenant.EntraTenantId);
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        var results = new List<TenantCheckResult>();

        var granted = await graph.GetGrantedApplicationPermissionsAsync(ct);
        var missing = Missing(granted);
        if (missing.Count > 0 && !freshToken)
        {
            // The cached token may predate the consent (its roles claim is fixed at issue time): retry once with a new one.
            graphFactory.Invalidate(tenant.EntraTenantId);
            graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
            missing = Missing(await graph.GetGrantedApplicationPermissionsAsync(ct));
        }
        results.Add(missing.Count == 0
            ? new(TenantCheckIds.GraphPermissions, "Berechtigungen der App erteilt", CheckStatus.Ok, "Alle benötigten Graph-Berechtigungen sind erteilt.", false, "")
            : new(TenantCheckIds.GraphPermissions, "Berechtigungen der App erteilt", CheckStatus.Failed,
                "Es fehlen Berechtigungen: " + string.Join(", ", missing) +
                ". Direkt nach dem Admin-Consent kann Entra einige Minuten brauchen, bis die Berechtigungen wirksam sind; dann „Erneut prüfen“.", false,
                "Den Admin-Consent erneut erteilen: in der Tenant-Übersicht „Consent erneut erteilen“ wählen, oder im Entra Admin Center → Unternehmensanwendungen → Yubikey Lifecycle Manager → Berechtigungen → „Administratorzustimmung erteilen“."));

        if (missing.Contains("Policy.Read.All", StringComparer.OrdinalIgnoreCase))
        {
            tenant.LastChecksJson = JsonSerializer.Serialize(results, Json);
            tenant.LastCheckAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return results;
        }

        await RefreshOrganizationAsync(tenant, graph, missing, ct);

        var policy = await graph.GetFido2PolicyAsync(ct);
        results.AddRange(Fido2PolicyAnalyzer.Analyze(policy, YubiKeyAaguids, tenant.TargetIncludeGroups));
        if (missing.Contains("Policy.ReadWrite.AuthenticationMethod", StringComparer.OrdinalIgnoreCase))
            results = results.Select(r => r with { CanFix = false }).ToList();

        tenant.LastChecksJson = JsonSerializer.Serialize(results, Json);
        tenant.PolicySnapshotJson = JsonSerializer.Serialize(policy, Json);
        tenant.LastCheckAt = DateTimeOffset.UtcNow;
        audit.Add(actor, AuditActions.TenantChecked, tenant.Id, details: results.Select(r => new { r.Id, Status = r.Status.ToString() }));
        await db.SaveChangesAsync(ct);
        return results;
    }

    private static List<string> Missing(IReadOnlyList<string> granted) =>
        SetupService.RequiredApplicationPermissions.Except(granted, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// If the organization could not be read during onboarding (e.g. permissions not yet effective), the tenant
    /// still carries its id as display name. Fill in name and domain as soon as the permissions work.
    /// </summary>
    private static async Task RefreshOrganizationAsync(Tenant tenant, IGraphClient graph, IReadOnlyCollection<string> missing, CancellationToken ct)
    {
        var placeholder = string.IsNullOrEmpty(tenant.DisplayName) || tenant.DisplayName == tenant.EntraTenantId.ToString();
        if ((!placeholder && !string.IsNullOrEmpty(tenant.DefaultDomain)) || missing.Contains("Organization.Read.All", StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            var org = await graph.GetOrganizationAsync(ct);
            if (placeholder && (!string.IsNullOrWhiteSpace(org.DisplayName) || !string.IsNullOrWhiteSpace(org.DefaultDomain)))
                tenant.DisplayName = string.IsNullOrWhiteSpace(org.DisplayName) ? org.DefaultDomain! : org.DisplayName;
            if (string.IsNullOrEmpty(tenant.DefaultDomain)) tenant.DefaultDomain = org.DefaultDomain;
        }
        catch (GraphException) { /* not essential for the checks */ }
    }

    /// <summary>"Jetzt beheben": patches the FIDO2 policy for the selected checks, then re-runs the checks.</summary>
    public async Task<IReadOnlyList<TenantCheckResult>> FixAsync(Tenant tenant, IReadOnlyCollection<string> checkIds, AuditActor actor, CancellationToken ct = default)
    {
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        var before = await graph.GetFido2PolicyAsync(ct);
        var after = Fido2PolicyAnalyzer.Fix(before, YubiKeyAaguids, tenant.TargetIncludeGroups, checkIds);
        if (after != before)
        {
            await graph.UpdateFido2PolicyAsync(after, ct);
            audit.Add(actor, AuditActions.TenantPolicyFixed, tenant.Id, details: new { checkIds, before, after });
            await db.SaveChangesAsync(ct);
        }
        return await RunAsync(tenant, actor, ct);
    }
}
