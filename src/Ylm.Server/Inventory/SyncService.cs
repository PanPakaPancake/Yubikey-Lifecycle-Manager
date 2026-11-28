using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Inventory;

public sealed record SyncResult(int UsersScanned, int Imported, int Updated, int Removed, int? TargetUsers);

/// <summary>Takes over YubiKey passkeys from Entra (planning 5.1, self-registered keys without serial/firmware).</summary>
public sealed class SyncService(YlmDbContext db, IGraphClientFactory graphFactory, AuditService audit, Reporting.ReminderService reminders, ILogger<SyncService> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<SyncResult> SyncAsync(Guid tenantId, AuditActor actor, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await SyncCoreAsync(tenantId, actor, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<SyncResult> SyncCoreAsync(Guid tenantId, AuditActor actor, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
        if (tenant.Status != TenantStatus.Active) throw ApiException.Conflict("tenant_inactive", "Der Tenant ist nicht aktiv.");
        var now = DateTimeOffset.UtcNow;
        try
        {
            var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
            var existing = await db.Registrations.Include(r => r.Key).Where(r => r.TenantId == tenantId).ToListAsync(ct);
            var byMethod = existing.ToDictionary(r => r.EntraMethodId);
            var seen = new HashSet<string>();
            int users = 0, imported = 0, updated = 0, removed = 0;

            await foreach (var userId in graph.ListUserIdsWithPasskeysAsync(ct))
            {
                users++;
                // Nutzungsdatum nur, wenn die Erinnerung „Lange nicht benutzt“ es braucht (beta-Endpunkt).
                var methods = tenant.RemindUnusedKeys ? await graph.ListFido2MethodsWithUsageAsync(userId, ct) : await graph.ListFido2MethodsAsync(userId, ct);
                var yubi = methods.Where(m => YubicoAaguids.IsYubiKey(m.AaGuid, m.Model)).ToList();
                if (yubi.Count == 0) continue;
                GraphUser? user = null;
                foreach (var m in yubi)
                {
                    seen.Add(m.Id);
                    if (byMethod.TryGetValue(m.Id, out var reg))
                    {
                        reg.LastSeenInSyncAt = now;
                        if (m.LastUsedDateTime is { } used && used != reg.LastUsedAt) reg.LastUsedAt = used;
                        if (reg.DisplayName != m.DisplayName) { reg.DisplayName = m.DisplayName; updated++; }
                        if (reg.RemovedAt is not null) { reg.RemovedAt = null; reg.RemovedReason = null; updated++; }
                        continue;
                    }
                    user ??= await graph.GetUserAsync(userId, ct);
                    var key = new YubiKey
                    {
                        TenantId = tenantId,
                        Source = KeySource.EntraSync,
                        Status = KeyStatus.Active,
                        Model = m.Model,
                        AaGuid = m.AaGuid,
                        CreatedAt = now,
                        UpdatedAt = now,
                        StatusChangedAt = m.CreatedDateTime ?? now,
                    };
                    reg = new KeyRegistration
                    {
                        TenantId = tenantId,
                        Key = key,
                        EntraUserId = userId,
                        UserPrincipalName = user?.UserPrincipalName ?? userId,
                        UserDisplayName = user?.DisplayName,
                        EntraMethodId = m.Id,
                        DisplayName = m.DisplayName,
                        AaGuid = m.AaGuid,
                        RegisteredAt = m.CreatedDateTime ?? now,
                        Source = KeySource.EntraSync,
                        LastSeenInSyncAt = now,
                        LastUsedAt = m.LastUsedDateTime,
                    };
                    db.Keys.Add(key);
                    db.Registrations.Add(reg);
                    byMethod[m.Id] = reg;
                    imported++;
                    audit.Add(actor, AuditActions.RegistrationImported, tenantId, key.Id, reg.UserPrincipalName, new { m.Model, m.AaGuid, m.DisplayName });
                }
            }

            foreach (var reg in existing.Where(r => r.RemovedAt is null && !seen.Contains(r.EntraMethodId)))
            {
                reg.RemovedAt = now;
                reg.RemovedReason = "In Entra nicht mehr vorhanden";
                removed++;
                audit.Add(actor, AuditActions.RegistrationRemovedInEntra, tenantId, reg.KeyId, reg.UserPrincipalName, new { reg.EntraMethodId });
                var key = reg.Key!;
                var stillActive = existing.Any(r => r.KeyId == key.Id && r.RemovedAt is null);
                // A key known only from Entra cannot be tracked physically once its passkey is gone.
                if (!stillActive && key.Source == KeySource.EntraSync && key.Status == KeyStatus.Active)
                {
                    key.Status = KeyStatus.Retired;
                    key.StatusNote = "Passkey in Entra entfernt";
                    key.StatusChangedAt = now;
                    key.UpdatedAt = now;
                }
            }

            if (tenant.RemindLeavers)
            {
                // Ein Durchlauf über alle Benutzer statt einer Abfrage je Person.
                var enabled = (await graph.ListDirectoryUsersAsync(ct)).ToDictionary(u => u.Id, u => u.AccountEnabled);
                foreach (var reg in byMethod.Values.Where(r => r.RemovedAt is null))
                {
                    bool? state = enabled.TryGetValue(reg.EntraUserId, out var en) ? en : null;
                    if (reg.UserAccountEnabled != state) reg.UserAccountEnabled = state;
                }
            }

            tenant.TargetUserCount = await TargetAudience.CountAsync(graph, tenant.TargetIncludeGroups, tenant.TargetExcludeGroups, ct) ?? tenant.TargetUserCount;
            tenant.LastSyncAt = now;
            tenant.LastSyncSucceeded = true;
            tenant.LastSyncMessage = $"{users} Personen mit Passkey geprüft, {imported} neu, {removed} entfernt.";
            audit.Add(actor, AuditActions.SyncCompleted, tenantId, details: new { users, imported, updated, removed });
            await db.SaveChangesAsync(ct);
            if (tenant.AnyReminderEnabled)
            {
                try { await reminders.EvaluateAsync(tenant, actor, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Erinnerungen für {Tenant} konnten nicht ausgewertet werden", tenant.DisplayName);
                    db.ChangeTracker.Clear();
                }
            }
            return new SyncResult(users, imported, updated, removed, tenant.TargetUserCount);
        }
        catch (Exception ex) when (ex is not ApiException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Sync failed for tenant {Tenant}", tenantId);
            db.ChangeTracker.Clear();
            var t = await db.Tenants.FirstAsync(x => x.Id == tenantId, ct);
            t.LastSyncAt = now;
            t.LastSyncSucceeded = false;
            t.LastSyncMessage = ex.Message;
            audit.Add(actor, AuditActions.SyncFailed, tenantId, details: new { error = ex.Message });
            await db.SaveChangesAsync(ct);
            throw;
        }
    }
}

/// <summary>Interval sync (planning 5.3) and deletion of offboarded tenant data after the retention period.</summary>
public sealed class BackgroundJobs(IServiceScopeFactory scopes, IOptions<YlmOptions> options, ILogger<BackgroundJobs> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnableBackgroundJobs) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Background job run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YlmDbContext>();
        var settings = await db.Installation.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings?.IsSetupComplete != true) return;

        var now = DateTimeOffset.UtcNow;
        var due = await db.Tenants.Where(t => t.Status == TenantStatus.Active).ToListAsync(ct);
        foreach (var t in due.Where(t => t.LastSyncAt is null || t.LastSyncAt.Value.AddMinutes(t.SyncIntervalMinutes) <= now))
        {
            using var s = scopes.CreateScope();
            try
            {
                await s.ServiceProvider.GetRequiredService<SyncService>().SyncAsync(t.Id, AuditService.System, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogInformation("Scheduled sync for {Tenant} failed: {Message}", t.DisplayName, ex.Message);
            }
        }

        try
        {
            using var c = scopes.CreateScope();
            await c.ServiceProvider.GetRequiredService<Ylm.Server.Secrets.CertificateRenewalService>().RunAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Prüfung des App-Zertifikats fehlgeschlagen");
        }

        var expired = await db.Tenants.Where(t => t.Status == TenantStatus.Offboarded && t.DataDeletionDueAt != null && t.DataDeletionDueAt <= now).ToListAsync(ct);
        foreach (var t in expired)
        {
            var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
            await db.Keys.Where(k => k.TenantId == t.Id).ExecuteDeleteAsync(ct);
            await db.Profiles.Where(p => p.TenantId == t.Id).ExecuteDeleteAsync(ct);
            await db.CustomFields.Where(f => f.TenantId == t.Id).ExecuteDeleteAsync(ct);
            await db.CustomFieldOverrides.Where(o => o.TenantId == t.Id).ExecuteDeleteAsync(ct);
            await db.GroupProfiles.Where(a => a.TenantId == t.Id).ExecuteDeleteAsync(ct);
            await db.Brandings.Where(b => b.TenantId == t.Id).ExecuteDeleteAsync(ct);
            await db.ReminderStates.Where(r => r.TenantId == t.Id).ExecuteDeleteAsync(ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE api_keys SET \"TenantIds\" = array_remove(\"TenantIds\", {0})", [t.Id], ct);
            t.DataDeletionDueAt = null;
            audit.Add(AuditService.System, AuditActions.TenantDataDeleted, t.Id, target: t.DisplayName);
            await db.SaveChangesAsync(ct);
        }
    }
}
