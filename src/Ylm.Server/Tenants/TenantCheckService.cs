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

    public async Task<IReadOnlyList<TenantCheckResult>> RunAsync(Tenant tenant, AuditActor actor, CancellationToken ct = default)
    {
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        var results = new List<TenantCheckResult>();

        var granted = await graph.GetGrantedApplicationPermissionsAsync(ct);
        var missing = SetupService.RequiredApplicationPermissions.Except(granted, StringComparer.OrdinalIgnoreCase).ToList();
        results.Add(missing.Count == 0
            ? new(TenantCheckIds.GraphPermissions, "Berechtigungen der App erteilt", CheckStatus.Ok, "Alle benötigten Graph-Berechtigungen sind erteilt.", false, "")
            : new(TenantCheckIds.GraphPermissions, "Berechtigungen der App erteilt", CheckStatus.Failed,
                "Es fehlen Berechtigungen: " + string.Join(", ", missing), false,
                "Den Admin-Consent erneut erteilen: in der Tenant-Übersicht „Consent erneut erteilen“ wählen, oder im Entra Admin Center → Unternehmensanwendungen → Yubikey Lifecycle Manager → Berechtigungen → „Administratorzustimmung erteilen“."));

        if (missing.Contains("Policy.Read.All", StringComparer.OrdinalIgnoreCase))
        {
            tenant.LastChecksJson = JsonSerializer.Serialize(results, Json);
            tenant.LastCheckAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return results;
        }

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
