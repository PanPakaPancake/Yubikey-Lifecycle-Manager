using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Rollout;

public sealed record RolloutUser(
    string Id, string Upn, string? DisplayName, string? Department, string? UserType, bool AccountEnabled, bool IsAdmin,
    IReadOnlyList<string> Methods, string Stage, string? DefaultMfaMethod, DateTimeOffset? LastSignIn, bool InTarget, int PlatformKeys);

public sealed record RolloutSnapshot(
    Guid TenantId, DateTimeOffset GeneratedAt, bool FromReport, bool HasSignInData, bool TargetDefined, IReadOnlyList<RolloutUser> Users);

public sealed record RolloutFilter(IReadOnlyCollection<string>? Stages, bool IncludeGuests, bool IncludeDisabled, bool OnlyTarget);

public sealed record TapResult(string UserId, string Upn, string? DisplayName, string? Pass, DateTimeOffset? StartDateTime, int? LifetimeInMinutes, bool? IsUsableOnce, string? Error);

public sealed record RemovalResult(string UserId, string Upn, int Removed, IReadOnlyList<string> RemovedLabels, string? Skipped, string? Error);

/// <summary>
/// Passkey-Rollout: wer hat schon einen Passkey, wer nur klassische MFA, wer gar nichts. Daten kommen live aus Entra
/// und werden pro Tenant kurz zwischengespeichert (der Microsoft-Bericht selbst hängt ohnehin etwas hinterher).
/// </summary>
public sealed class RolloutService(YlmDbContext db, IGraphClientFactory graphFactory, IMemoryCache cache, AuditService audit, Protection.ProtectedAccountService protection)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    public const int MaxUsersPerAction = 100;

    private static string Key(Guid tenantId) => "rollout:" + tenantId;

    public async Task<RolloutSnapshot> GetAsync(Tenant tenant, bool refresh, CancellationToken ct = default)
    {
        if (cache.TryGetValue(Key(tenant.Id), out RolloutSnapshot? cached) && cached is not null
            && (!refresh || DateTimeOffset.UtcNow - cached.GeneratedAt < TimeSpan.FromSeconds(30)))
            return cached;
        var snapshot = await BuildAsync(tenant, ct);
        cache.Set(Key(tenant.Id), snapshot, CacheDuration);
        return snapshot;
    }

    private async Task<RolloutSnapshot> BuildAsync(Tenant tenant, CancellationToken ct)
    {
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        var regTask = graph.ListUserRegistrationDetailsAsync(ct);
        var dirTask = graph.ListDirectoryUsersAsync(ct);
        await Task.WhenAll(regTask, dirTask);
        var reg = regTask.Result.Users.ToDictionary(r => r.Id);
        var users = dirTask.Result;

        // Zielgruppe wie im Dashboard: Einschlussgruppen (leer = alle) ohne Ausschlussgruppen.
        HashSet<string>? include = null;
        if (tenant.TargetIncludeGroups.Count > 0)
        {
            include = [];
            foreach (var g in tenant.TargetIncludeGroups) include.UnionWith(await graph.ListUserIdsAsync(g.Id, ct));
        }
        var exclude = new HashSet<string>();
        foreach (var g in tenant.TargetExcludeGroups) exclude.UnionWith(await graph.ListUserIdsAsync(g.Id, ct));

        var keys = await db.Registrations.AsNoTracking()
            .Where(r => r.TenantId == tenant.Id && r.RemovedAt == null)
            .GroupBy(r => r.EntraUserId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var rows = users.Select(u =>
        {
            reg.TryGetValue(u.Id, out var r);
            var methods = r?.MethodsRegistered ?? [];
            return new RolloutUser(u.Id, u.UserPrincipalName, u.DisplayName, u.Department, u.UserType ?? r?.UserType, u.AccountEnabled, r?.IsAdmin ?? false,
                methods, AuthMethodCatalog.StageOf(methods).ToString(), r?.DefaultMfaMethod, u.LastSignIn,
                (include is null || include.Contains(u.Id)) && !exclude.Contains(u.Id), keys.GetValueOrDefault(u.Id));
        }).OrderBy(u => u.DisplayName ?? u.Upn, StringComparer.CurrentCultureIgnoreCase).ToList();

        return new RolloutSnapshot(tenant.Id, DateTimeOffset.UtcNow, regTask.Result.FromReport, users.Any(u => u.LastSignIn is not null),
            tenant.TargetIncludeGroups.Count > 0 || tenant.TargetExcludeGroups.Count > 0, rows);
    }

    public static IEnumerable<RolloutUser> Apply(RolloutSnapshot s, RolloutFilter f) => s.Users.Where(u =>
        (f.IncludeGuests || !string.Equals(u.UserType, "Guest", StringComparison.OrdinalIgnoreCase))
        && (f.IncludeDisabled || u.AccountEnabled)
        && (!f.OnlyTarget || u.InTarget)
        && (f.Stages is not { Count: > 0 } || f.Stages.Contains(u.Stage, StringComparer.OrdinalIgnoreCase)));

    /// <summary>Kennzahlen pro Stufe für die gefilterte Menge.</summary>
    public static Dictionary<string, int> CountStages(IEnumerable<RolloutUser> users)
    {
        var d = Enum.GetNames<RolloutStage>().ToDictionary(n => n, _ => 0);
        foreach (var u in users) d[u.Stage] = d.GetValueOrDefault(u.Stage) + 1;
        return d;
    }

    /// <summary>Nach einer Änderung den Benutzer im Zwischenspeicher mit den Live-Methoden aktualisieren.</summary>
    private void UpdateCached(Guid tenantId, string userId, IReadOnlyList<GraphAuthMethod> live)
    {
        if (!cache.TryGetValue(Key(tenantId), out RolloutSnapshot? s) || s is null) return;
        var methods = live.Select(m => m.ReportName).Distinct().ToList();
        var users = s.Users.Select(u => u.Id == userId ? u with { Methods = methods, Stage = AuthMethodCatalog.StageOf(methods).ToString() } : u).ToList();
        cache.Set(Key(tenantId), s with { Users = users }, CacheDuration);
    }

    private RolloutUser? Cached(Guid tenantId, string userId) =>
        cache.TryGetValue(Key(tenantId), out RolloutSnapshot? s) ? s?.Users.FirstOrDefault(u => u.Id == userId) : null;

    private async Task<(string Upn, string? Name)> UserLabelAsync(Tenant tenant, IGraphClient graph, string userId, CancellationToken ct)
    {
        if (Cached(tenant.Id, userId) is { } c) return (c.Upn, c.DisplayName);
        var u = await graph.GetUserAsync(userId, ct) ?? throw ApiException.NotFound("Benutzer");
        return (u.UserPrincipalName, u.DisplayName);
    }

    public static object ToDto(GraphAuthMethod m)
    {
        var info = AuthMethodCatalog.Get(m.ReportName);
        return new
        {
            m.Id, m.Kind, m.ReportName, Label = info.Label, Strength = info.Strength.ToString(), info.IsPasskey,
            m.DisplayName, m.Detail, m.CreatedDateTime, Removable = AuthMethodCatalog.IsRemovable(m.Kind),
            // Windows Hello for Business: Entra nennt im Anzeigenamen das Gerät, falls die Geräteabfrage nichts liefert.
            Device = m.Device ?? (m.Kind is "windowsHelloForBusiness" or "platformCredential" ? m.DisplayName : null),
            info.IsSecurityKey,
        };
    }

    public async Task<IReadOnlyList<GraphAuthMethod>> ListMethodsAsync(Tenant tenant, string userId, CancellationToken ct = default)
    {
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        var live = await graph.ListAuthenticationMethodsAsync(userId, ct);
        UpdateCached(tenant.Id, userId, live);
        return live;
    }

    /// <summary>Für das Audit-Log: Telefonnummern und Mailadressen nur gekürzt.</summary>
    private static string? Mask(GraphAuthMethod m) => m.Kind switch
    {
        "phone" when m.Detail is { Length: > 4 } d => "…" + d[^4..],
        "email" when m.Detail is { } e && e.IndexOf('@') > 1 => e[0] + "…" + e[e.IndexOf('@')..],
        _ => m.DisplayName ?? m.Detail,
    };

    public async Task<IReadOnlyList<GraphAuthMethod>> RemoveMethodAsync(Tenant tenant, string userId, string kind, string methodId, PlatformUser actorUser, CancellationToken ct = default)
    {
        if (!AuthMethodCatalog.IsRemovable(kind))
            throw ApiException.BadRequest("not_removable", kind == "fido2"
                ? "Passkeys auf Sicherheitsschlüsseln bitte über das Inventar sperren, damit der Key-Status stimmt."
                : "Diese Methode kann hier nicht entfernt werden.");
        var actor = AuditService.User(actorUser);
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        await protection.EnsureAllowedAsync(tenant, graph, actorUser, [userId], ct);
        var before = await graph.ListAuthenticationMethodsAsync(userId, ct);
        var method = before.FirstOrDefault(m => m.Id == methodId && m.Kind == kind) ?? throw ApiException.NotFound("Methode");
        var (upn, _) = await UserLabelAsync(tenant, graph, userId, ct);
        await graph.DeleteAuthenticationMethodAsync(userId, kind, methodId, ct);
        audit.Add(actor, AuditActions.AuthMethodRemoved, tenant.Id, target: upn,
            details: new { userId, method = AuthMethodCatalog.Get(method.ReportName).Label, kind, detail = Mask(method) });
        await db.SaveChangesAsync(ct);
        var after = before.Where(m => m.Id != methodId).ToList();
        UpdateCached(tenant.Id, userId, after);
        return after;
    }

    /// <summary>
    /// Entfernt die gewählten Methodentypen bei mehreren Benutzern. Mit <paramref name="keepPhishingResistantOnly"/> nur bei
    /// Benutzern, die danach noch eine phishing-resistente Methode haben, damit niemand ohne MFA dasteht.
    /// </summary>
    public async Task<IReadOnlyList<RemovalResult>> RemoveBulkAsync(Tenant tenant, IReadOnlyCollection<string> userIds, IReadOnlyCollection<string> kinds,
        bool keepPhishingResistantOnly, PlatformUser actorUser, CancellationToken ct = default)
    {
        var actor = AuditService.User(actorUser);
        Validate(userIds);
        var bad = kinds.Where(k => !AuthMethodCatalog.IsRemovable(k)).ToList();
        if (kinds.Count == 0 || bad.Count > 0) throw ApiException.BadRequest("invalid", "Bitte nur entfernbare Methodentypen wählen.");
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        var results = new List<RemovalResult>();
        foreach (var userId in userIds.Distinct())
        {
            string upn = userId;
            try
            {
                (upn, _) = await UserLabelAsync(tenant, graph, userId, ct);
                if (await protection.DenyReasonAsync(tenant, graph, actorUser, userId, ct) is { } denied)
                {
                    results.Add(new(userId, upn, 0, [], denied, null));
                    continue;
                }
                var live = await graph.ListAuthenticationMethodsAsync(userId, ct);
                var remove = live.Where(m => kinds.Contains(m.Kind)).ToList();
                var remaining = live.Except(remove).ToList();
                if (remove.Count == 0) { results.Add(new(userId, upn, 0, [], "Keine passende Methode registriert.", null)); continue; }
                if (keepPhishingResistantOnly && !remaining.Any(m => AuthMethodCatalog.Get(m.ReportName).Strength == MethodStrength.PhishingResistant))
                {
                    results.Add(new(userId, upn, 0, [], "Übersprungen: hätte danach keinen Passkey bzw. keine phishing-resistente Methode.", null));
                    continue;
                }
                var labels = new List<string>();
                foreach (var m in remove)
                {
                    await graph.DeleteAuthenticationMethodAsync(userId, m.Kind, m.Id, ct);
                    var label = AuthMethodCatalog.Get(m.ReportName).Label;
                    labels.Add(label);
                    audit.Add(actor, AuditActions.AuthMethodRemoved, tenant.Id, target: upn, details: new { userId, method = label, m.Kind, detail = Mask(m), bulk = true });
                }
                await db.SaveChangesAsync(ct);
                UpdateCached(tenant.Id, userId, live.Where(m => !remove.Contains(m)).ToList());
                results.Add(new(userId, upn, remove.Count, labels, null, null));
            }
            catch (GraphException ex)
            {
                await db.SaveChangesAsync(ct);
                results.Add(new(userId, upn, 0, [], null, ex.Message));
            }
            catch (ApiException ex)
            {
                results.Add(new(userId, upn, 0, [], null, ex.Message));
            }
        }
        return results;
    }

    private static void Validate(IReadOnlyCollection<string> userIds)
    {
        if (userIds.Count == 0) throw ApiException.BadRequest("invalid", "Bitte mindestens einen Benutzer wählen.");
        if (userIds.Count > MaxUsersPerAction) throw ApiException.BadRequest("invalid", $"Höchstens {MaxUsersPerAction} Benutzer auf einmal.");
    }

    public async Task<GraphTapPolicy> GetTapPolicyAsync(Tenant tenant, CancellationToken ct = default) =>
        await (await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct)).GetTapPolicyAsync(ct);

    public async Task<GraphTapPolicy> EnableTapPolicyAsync(Tenant tenant, AuditActor actor, CancellationToken ct = default)
    {
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        await graph.EnableTapPolicyAsync(ct);
        audit.Add(actor, AuditActions.TapPolicyEnabled, tenant.Id);
        await db.SaveChangesAsync(ct);
        return await graph.GetTapPolicyAsync(ct);
    }

    /// <summary>Erzeugt TAPs. Die Codes werden nicht gespeichert und nicht protokolliert, nur einmal zurückgegeben.</summary>
    public async Task<IReadOnlyList<TapResult>> CreateTapsAsync(Tenant tenant, IReadOnlyCollection<string> userIds, DateTimeOffset? start, int? lifetime,
        bool? usableOnce, bool replaceExisting, PlatformUser actorUser, CancellationToken ct = default)
    {
        var actor = AuditService.User(actorUser);
        Validate(userIds);
        if (lifetime is < 10 or > 43200) throw ApiException.BadRequest("invalid", "Die Gültigkeit muss zwischen 10 Minuten und 30 Tagen liegen.");
        if (start is { } s && s < DateTimeOffset.UtcNow.AddMinutes(-5)) throw ApiException.BadRequest("invalid", "Der Beginn darf nicht in der Vergangenheit liegen.");
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        var results = new List<TapResult>();
        foreach (var userId in userIds.Distinct())
        {
            string upn = userId;
            string? name = null;
            try
            {
                (upn, name) = await UserLabelAsync(tenant, graph, userId, ct);
                if (await protection.DenyReasonAsync(tenant, graph, actorUser, userId, ct) is { } denied)
                {
                    results.Add(new(userId, upn, name, null, null, null, null, denied));
                    continue;
                }
                var live = await graph.ListAuthenticationMethodsAsync(userId, ct);
                var existing = live.Where(m => m.Kind == "temporaryAccessPass").ToList();
                if (existing.Count > 0 && !replaceExisting)
                {
                    results.Add(new(userId, upn, name, null, null, null, null, "Hat bereits einen TAP. „Vorhandenen TAP ersetzen“ wählen, um einen neuen zu erzeugen."));
                    continue;
                }
                foreach (var m in existing) await graph.DeleteAuthenticationMethodAsync(userId, m.Kind, m.Id, ct);
                var tap = await graph.CreateTemporaryAccessPassAsync(userId, start, lifetime, usableOnce, ct);
                audit.Add(actor, AuditActions.TapCreated, tenant.Id, target: upn,
                    details: new { userId, tap.StartDateTime, tap.LifetimeInMinutes, tap.IsUsableOnce, replaced = existing.Count > 0 });
                await db.SaveChangesAsync(ct);
                UpdateCached(tenant.Id, userId, [.. live.Except(existing), new GraphAuthMethod(tap.Id, "temporaryAccessPass", "temporaryAccessPass", null, "gültig", DateTimeOffset.UtcNow)]);
                results.Add(new(userId, upn, name, tap.Pass, tap.StartDateTime, tap.LifetimeInMinutes, tap.IsUsableOnce, null));
            }
            catch (GraphException ex)
            {
                results.Add(new(userId, upn, name, null, null, null, null, TapError(ex)));
            }
            catch (ApiException ex)
            {
                results.Add(new(userId, upn, name, null, null, null, null, ex.Message));
            }
        }
        return results;
    }

    private static string TapError(GraphException ex) =>
        ex.Message.Contains("policy", StringComparison.OrdinalIgnoreCase) || ex.GraphCode == "badRequest" && ex.Message.Contains("not enabled", StringComparison.OrdinalIgnoreCase)
            ? "Die Methode „Befristeter Zugriffspass“ ist für diesen Benutzer nicht aktiviert. " + ex.Message
            : ex.Message;
}
