using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;

namespace Ylm.Server.Reporting;

/// <summary>Eine offene Lebenszyklus-Erinnerung.</summary>
/// <param name="Subject">Stabile Kennung innerhalb von Tenant und Art (zum Ausblenden).</param>
/// <param name="Since">Bezugsdatum: letzte Nutzung, Registrierung bzw. Statuswechsel.</param>
public sealed record ReminderFinding(
    string Kind, Guid TenantId, string TenantName, string Subject,
    Guid? KeyId, int? SerialNumber, string? Model, string? KeyStatus, string? Usage,
    string? EntraUserId, string? UserPrincipalName, string? UserDisplayName,
    DateTimeOffset? Since, string Detail,
    DateTimeOffset? FirstSeenAt, bool Dismissed, string? DismissedByName, DateTimeOffset? DismissedAt, string? DismissNote, Guid? TodoId);

/// <summary>Einstellungen der Erinnerungen eines Tenants (für Übersicht und Seite).</summary>
public sealed record ReminderTenantSettings(Guid TenantId, string TenantName, bool UnusedKeys, int UnusedKeyDays, bool NoSpareKey, bool Leavers, bool RetiredWithPasskey, bool CreateTodos);

public sealed record ReminderCount(string Kind, int Count);

public sealed record ReminderOverview(
    IReadOnlyList<ReminderTenantSettings> Tenants, IReadOnlyList<ReminderCount> Counts, IReadOnlyList<ReminderFinding> Items,
    int KeysWithoutUsageDate);

/// <summary>
/// Optionale Lebenszyklus-Erinnerungen (pro Tenant einzeln einschaltbar, standardmäßig aus):
/// lange ungenutzte Keys, Personen ohne Ersatz-Key, ausgeschiedene Personen mit aktivem Key und
/// gesperrte bzw. ausgemusterte Keys, deren Passkey in Entra noch angemeldet ist.
/// Die Liste wird bei jedem Aufruf aus dem Inventar berechnet; <see cref="ReminderState"/> merkt sich nur,
/// was schon gemeldet oder ausgeblendet ist.
/// </summary>
public sealed class ReminderService(YlmDbContext db, AuditService audit)
{
    public static readonly IReadOnlyList<ReminderKind> Kinds = Enum.GetValues<ReminderKind>();

    /// <summary>Ereignis im Audit-Log (und damit für Webhooks und Mail-Alarme) je Art.</summary>
    public static string EventFor(ReminderKind kind) => kind switch
    {
        ReminderKind.UnusedKey => AuditActions.ReminderUnusedKeys,
        ReminderKind.NoSpareKey => AuditActions.ReminderNoSpareKey,
        ReminderKind.Leaver => AuditActions.ReminderLeavers,
        _ => AuditActions.ReminderRetiredWithPasskey,
    };

    public static bool IsEnabled(Tenant t, ReminderKind kind) => kind switch
    {
        ReminderKind.UnusedKey => t.RemindUnusedKeys,
        ReminderKind.NoSpareKey => t.RemindNoSpareKey,
        ReminderKind.Leaver => t.RemindLeavers,
        _ => t.RemindRetiredWithPasskey,
    };

    public static ReminderTenantSettings SettingsOf(Tenant t) =>
        new(t.Id, t.DisplayName, t.RemindUnusedKeys, t.UnusedKeyDays, t.RemindNoSpareKey, t.RemindLeavers, t.RemindRetiredWithPasskey, t.ReminderCreateTodos);

    /// <summary>Berechnet die Erinnerungen der Tenants (nur eingeschaltete Arten), mit Stand des Ausblendens.</summary>
    public async Task<ReminderOverview> BuildAsync(IReadOnlyList<Guid> tenantIds, bool includeDismissed = false, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        var tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id) && t.Status == TenantStatus.Active).OrderBy(t => t.DisplayName).ToListAsync(ct);
        var enabled = tenants.Where(t => t.AnyReminderEnabled).ToList();
        var ids = enabled.Select(t => t.Id).ToList();
        var (raw, withoutUsage) = await ComputeAsync(enabled, now ?? DateTimeOffset.UtcNow, ct);
        var states = await db.ReminderStates.AsNoTracking().Where(s => ids.Contains(s.TenantId)).ToListAsync(ct);
        var byKey = states.ToDictionary(s => (s.TenantId, s.Kind, s.Subject));
        var items = raw.Select(f =>
        {
            var s = byKey.GetValueOrDefault((f.TenantId, Enum.Parse<ReminderKind>(f.Kind), f.Subject));
            return s is null ? f : f with { FirstSeenAt = s.FirstSeenAt, Dismissed = s.DismissedAt != null, DismissedAt = s.DismissedAt, DismissedByName = s.DismissedByName, DismissNote = s.DismissNote, TodoId = s.TodoId };
        }).ToList();
        var counts = Kinds.Select(k => new ReminderCount(k.ToString(), items.Count(i => i.Kind == k.ToString() && !i.Dismissed))).ToList();
        return new ReminderOverview(tenants.Select(SettingsOf).ToList(), counts, includeDismissed ? items : items.Where(i => !i.Dismissed).ToList(), withoutUsage);
    }

    private async Task<(List<ReminderFinding> Findings, int KeysWithoutUsage)> ComputeAsync(IReadOnlyList<Tenant> tenants, DateTimeOffset now, CancellationToken ct)
    {
        var result = new List<ReminderFinding>();
        if (tenants.Count == 0) return (result, 0);
        var ids = tenants.Select(t => t.Id).ToList();
        var keys = await db.Keys.AsNoTracking()
            .Where(k => k.TenantId != null && ids.Contains(k.TenantId.Value) && k.Status != KeyStatus.InStock)
            .Select(k => new
            {
                k.Id, k.SerialNumber, k.Model, k.Status, k.Usage, k.StatusChangedAt, TenantId = k.TenantId!.Value,
                Regs = k.Registrations.Select(r => new { r.Id, r.EntraUserId, r.UserPrincipalName, r.UserDisplayName, r.RegisteredAt, r.LastUsedAt, r.UserAccountEnabled, r.RemovedAt }).ToList(),
            }).ToListAsync(ct);
        var withoutUsage = 0;

        foreach (var t in tenants)
        {
            var tk = keys.Where(k => k.TenantId == t.Id).ToList();
            ReminderFinding F(ReminderKind kind, string subject, Guid? keyId, int? serial, string? model, KeyStatus? status, KeyUsage? usage,
                string? userId, string? upn, string? name, DateTimeOffset? since, string detail) =>
                new(kind.ToString(), t.Id, t.DisplayName, subject, keyId, serial, model, status?.ToString(), usage?.ToString(), userId, upn, name, since, detail,
                    null, false, null, null, null, null);

            if (t.RemindUnusedKeys)
            {
                var limit = now.AddDays(-Math.Max(1, t.UnusedKeyDays));
                // Breakglass-Keys bleiben absichtlich im Umschlag; sie fallen nicht unter „lange nicht benutzt“.
                foreach (var k in tk.Where(k => k.Status == KeyStatus.Active && k.Usage != KeyUsage.Breakglass))
                {
                    var active = k.Regs.Where(r => r.RemovedAt == null).ToList();
                    if (active.Count == 0) continue;
                    var last = active.Max(r => r.LastUsedAt);
                    if (last is null) { withoutUsage++; continue; }
                    if (last >= limit) continue;
                    var who = active.OrderBy(r => r.RegisteredAt).First();
                    var days = (int)(now - last.Value).TotalDays;
                    result.Add(F(ReminderKind.UnusedKey, k.Id.ToString(), k.Id, k.SerialNumber, k.Model, k.Status, k.Usage, who.EntraUserId, who.UserPrincipalName, who.UserDisplayName,
                        last, $"Seit {days} Tagen nicht benutzt" + (active.Count > 1 ? $" ({active.Count} Konten)" : "")));
                }
            }

            if (t.RemindNoSpareKey)
            {
                var perUser = tk.Where(k => k.Status == KeyStatus.Active)
                    .SelectMany(k => k.Regs.Where(r => r.RemovedAt == null && r.UserAccountEnabled != false).Select(r => (Key: k, Reg: r)))
                    .GroupBy(x => x.Reg.EntraUserId);
                foreach (var g in perUser)
                {
                    var distinct = g.Select(x => x.Key.Id).Distinct().Count();
                    if (distinct != 1) continue;
                    var (k, r) = g.First();
                    // Wer nur einen geteilten oder Breakglass-Key hat, hat keinen eigenen; ein Ersatz-Key ist dort keine sinnvolle Frage.
                    if (k.Usage != KeyUsage.Personal) continue;
                    result.Add(F(ReminderKind.NoSpareKey, r.EntraUserId, k.Id, k.SerialNumber, k.Model, k.Status, k.Usage, r.EntraUserId, r.UserPrincipalName, r.UserDisplayName,
                        r.RegisteredAt, "Nur ein Key, kein Ersatz-Key"));
                }
            }

            if (t.RemindLeavers)
            {
                foreach (var k in tk.Where(k => k.Status == KeyStatus.Active))
                {
                    var active = k.Regs.Where(r => r.RemovedAt == null).ToList();
                    foreach (var r in active.Where(r => r.UserAccountEnabled == false))
                        result.Add(F(ReminderKind.Leaver, $"{k.Id}:{r.EntraUserId}", k.Id, k.SerialNumber, k.Model, k.Status, k.Usage, r.EntraUserId, r.UserPrincipalName, r.UserDisplayName,
                            r.RegisteredAt, "Konto in Entra deaktiviert, Key noch aktiv"));
                    if (active.Count == 0 && k.Regs.Count > 0)
                    {
                        // Alle Passkeys sind in Entra verschwunden (z. B. Konto gelöscht), der Key gilt aber noch als ausgegeben.
                        var last = k.Regs.OrderByDescending(r => r.RemovedAt).First();
                        result.Add(F(ReminderKind.Leaver, k.Id.ToString(), k.Id, k.SerialNumber, k.Model, k.Status, k.Usage, last.EntraUserId, last.UserPrincipalName, last.UserDisplayName,
                            last.RemovedAt, "Kein Konto mehr auf dem Key (in Entra entfernt oder gelöscht), Key noch als aktiv geführt"));
                    }
                }
            }

            if (t.RemindRetiredWithPasskey)
            {
                foreach (var k in tk.Where(k => k.Status is KeyStatus.Locked or KeyStatus.Retired))
                {
                    var active = k.Regs.Where(r => r.RemovedAt == null).ToList();
                    if (active.Count == 0) continue;
                    var who = active.OrderBy(r => r.RegisteredAt).First();
                    result.Add(F(ReminderKind.RetiredWithPasskey, k.Id.ToString(), k.Id, k.SerialNumber, k.Model, k.Status, k.Usage, who.EntraUserId, who.UserPrincipalName, who.UserDisplayName,
                        k.StatusChangedAt, (k.Status == KeyStatus.Locked ? "Gesperrt" : "Ausgemustert") + $", aber {active.Count} Passkey(s) in Entra noch angemeldet"));
                }
            }
        }
        return (result, withoutUsage);
    }

    /// <summary>
    /// Nach jedem Sync (und nach Änderung der Einstellungen): neue Erinnerungen merken und einmal melden,
    /// je Art ein Eintrag im Audit-Log (für Webhooks und Mail-Alarme), optional ToDos für fehlende Ersatz-Keys.
    /// Erledigte Erinnerungen werden vergessen, damit sie bei erneutem Auftreten wieder gemeldet werden.
    /// </summary>
    public async Task<int> EvaluateAsync(Tenant tenant, AuditActor actor, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var (findings, _) = await ComputeAsync([tenant], at, ct);
        var states = await db.ReminderStates.Where(s => s.TenantId == tenant.Id).ToListAsync(ct);
        var current = findings.Select(f => (Kind: Enum.Parse<ReminderKind>(f.Kind), f.Subject)).ToHashSet();
        // Nur bei eingeschalteter Art aufräumen; beim Abschalten bleibt das Ausgeblendete erhalten.
        db.ReminderStates.RemoveRange(states.Where(s => IsEnabled(tenant, s.Kind) && !current.Contains((s.Kind, s.Subject))));
        var known = states.Select(s => (s.Kind, s.Subject)).ToHashSet();
        var fresh = findings.Where(f => !known.Contains((Enum.Parse<ReminderKind>(f.Kind), f.Subject))).ToList();
        var openTodoUsers = tenant.ReminderCreateTodos
            ? (await db.Todos.Where(x => x.TenantId == tenant.Id && x.Status == TodoStatus.Open).Select(x => x.EntraUserId).ToListAsync(ct)).ToHashSet()
            : [];

        foreach (var f in fresh)
        {
            var state = new ReminderState { TenantId = tenant.Id, Kind = Enum.Parse<ReminderKind>(f.Kind), Subject = f.Subject, FirstSeenAt = at };
            if (state.Kind == ReminderKind.NoSpareKey && tenant.ReminderCreateTodos && f.EntraUserId is { } uid && openTodoUsers.Add(uid))
            {
                var todo = new RegistrationTodo
                {
                    TenantId = tenant.Id, EntraUserId = uid, UserPrincipalName = f.UserPrincipalName ?? uid, UserDisplayName = f.UserDisplayName,
                    Note = $"Ersatz-Key ausgeben: bisher nur Key {f.SerialNumber?.ToString() ?? f.Model ?? ""}".TrimEnd(),
                    Source = "Erinnerung: Ersatz-Key", CreatedByName = "Erinnerungen",
                };
                db.Todos.Add(todo);
                state.TodoId = todo.Id;
                audit.Add(actor, AuditActions.TodoCreated, tenant.Id, target: todo.UserPrincipalName, details: new { todo = todo.Id, source = todo.Source });
            }
            db.ReminderStates.Add(state);
        }

        foreach (var group in fresh.GroupBy(f => Enum.Parse<ReminderKind>(f.Kind)))
        {
            var list = group.ToList();
            audit.Add(actor, EventFor(group.Key), tenant.Id, target: $"{list.Count} neu", details: new
            {
                count = list.Count,
                total = findings.Count(f => f.Kind == group.Key.ToString()),
                days = group.Key == ReminderKind.UnusedKey ? tenant.UnusedKeyDays : (int?)null,
                // Höchstens 25 Einträge, damit Webhook und Mail lesbar bleiben; der Rest steht unter „Erinnerungen“.
                items = list.Take(25).Select(f => new { serialNumber = f.SerialNumber, model = f.Model, userPrincipalName = f.UserPrincipalName, userDisplayName = f.UserDisplayName, since = f.Since, detail = f.Detail }),
            });
        }
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }

    public async Task SetDismissedAsync(Guid tenantId, ReminderKind kind, string subject, bool dismissed, string? note, AuditActor actor, CancellationToken ct = default)
    {
        var s = await db.ReminderStates.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Kind == kind && x.Subject == subject, ct);
        if (s is null)
        {
            if (!dismissed) return;
            s = new ReminderState { TenantId = tenantId, Kind = kind, Subject = subject };
            db.ReminderStates.Add(s);
        }
        s.DismissedAt = dismissed ? DateTimeOffset.UtcNow : null;
        s.DismissedByName = dismissed ? actor.Name : null;
        s.DismissNote = dismissed ? (string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 300)]) : null;
        audit.Add(actor, dismissed ? AuditActions.ReminderDismissed : AuditActions.ReminderRestored, tenantId, target: subject, details: new { kind = kind.ToString(), note = s.DismissNote });
        await db.SaveChangesAsync(ct);
    }
}
