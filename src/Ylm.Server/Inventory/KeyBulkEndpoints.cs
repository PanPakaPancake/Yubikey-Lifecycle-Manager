using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Inventory;

/// <summary>Mehrfachauswahl im Lager: mehrere Keys auf einmal löschen, bearbeiten, zuweisen, ins Partnerlager legen oder ausmustern.</summary>
public static class KeyBulkEndpoints
{
    public enum BulkAction { Delete, Update, Assign, ToPartnerStock, Retire }

    /// <param name="AddTags">Update: Tags, die ergänzt werden.</param>
    /// <param name="RemoveTags">Update: Tags, die entfernt werden.</param>
    /// <param name="CustomFields">Update: Zusatzfelder, die gesetzt werden (leerer Wert löscht das Feld). Nicht genannte Felder bleiben.</param>
    /// <param name="TenantId">Assign: Ziel-Tenant.</param>
    public sealed record BulkRequest(BulkAction Action, List<Guid> Ids, List<string>? AddTags = null, List<string>? RemoveTags = null,
        Dictionary<string, string?>? CustomFields = null, Guid? TenantId = null, string? Note = null, bool ToPartnerStock = false);

    public const int MaxKeys = 500;

    public static bool CanDelete(YubiKey k) =>
        k.Status is KeyStatus.InStock or KeyStatus.Retired && k.Registrations.All(r => r.RemovedAt is not null);

    public static async Task DeleteAsync(YubiKey key, PlatformUser user, YlmDbContext db, AuditService audit)
    {
        if (!CanDelete(key))
            throw ApiException.Conflict("not_deletable", L.T("Nur Keys im Lager oder ausgemusterte Keys ohne aktive Registrierung können gelöscht werden. Aktive Keys bitte zuerst sperren oder zurücksetzen."));
        audit.Add(AuditService.User(user), AuditActions.KeyDeleted, key.TenantId, key.Id, key.SerialNumber?.ToString(),
            new { key.SerialNumber, key.Model, key.FirmwareVersion, status = key.Status.ToString(), key.Tags, key.CustomFields, key.Notes, partnerStock = key.TenantId is null });
        db.Keys.Remove(key);
        await db.SaveChangesAsync();
    }

    public static void MapKeyBulk(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapDelete("/keys/{id:guid}", async (Guid id, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var key = await db.Keys.Include(k => k.Registrations).FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound(L.T("Key"));
            var user = await cu.RequireKeyAccessAsync(key.TenantId, Permission.ManageInventory);
            await DeleteAsync(key, user, db, audit);
            return Results.NoContent();
        });

        g.MapPost("/keys/bulk", async (BulkRequest body, CurrentUser cu, YlmDbContext db, KeyService keys, AuditService audit) =>
        {
            var ids = (body.Ids ?? []).Distinct().ToList();
            if (ids.Count == 0) throw ApiException.BadRequest("invalid", L.T("Keine Keys ausgewählt."));
            if (ids.Count > MaxKeys) throw ApiException.BadRequest("invalid", L.F($"Höchstens {MaxKeys} Keys auf einmal."));
            if (!Enum.IsDefined(body.Action)) throw ApiException.BadRequest("invalid", L.T("Unbekannte Aktion."));
            if (body.Action == BulkAction.Assign && body.TenantId is null) throw ApiException.BadRequest("invalid", L.T("Bitte einen Tenant wählen."));
            await cu.GetAsync();

            var done = new List<Guid>();
            var failed = new List<object>();
            foreach (var id in ids)
            {
                var key = await db.Keys.Include(k => k.Registrations).Include(k => k.Tenant).FirstOrDefaultAsync(k => k.Id == id);
                if (key is null) { failed.Add(new { id, serialNumber = (int?)null, error = L.T("Key nicht gefunden.") }); continue; }
                try
                {
                    await ApplyAsync(key, body, cu, db, keys, audit);
                    done.Add(id);
                }
                catch (Exception e) when (e is ApiException or DomainException)
                {
                    db.ChangeTracker.Clear();
                    failed.Add(new { id, key.SerialNumber, error = e.Message });
                }
            }
            return Results.Ok(new { done, failed });
        });
    }

    private static async Task ApplyAsync(YubiKey key, BulkRequest body, CurrentUser cu, YlmDbContext db, KeyService keys, AuditService audit)
    {
        switch (body.Action)
        {
            case BulkAction.Delete:
            {
                var user = await cu.RequireKeyAccessAsync(key.TenantId, Permission.ManageInventory);
                await DeleteAsync(key, user, db, audit);
                break;
            }
            case BulkAction.Update:
            {
                var user = await cu.RequireKeyAccessAsync(key.TenantId, Permission.ManageInventory);
                var before = new { key.Tags, key.CustomFields };
                var tags = key.Tags.Concat(body.AddTags ?? [])
                    .Where(t => !(body.RemoveTags ?? []).Contains(t, StringComparer.OrdinalIgnoreCase));
                var newTags = Tags.Normalize(tags).ToList();
                var fields = key.CustomFields;
                if (body.CustomFields is { Count: > 0 })
                {
                    var merged = new Dictionary<string, string?>(key.CustomFields, StringComparer.OrdinalIgnoreCase);
                    foreach (var (k, v) in body.CustomFields) merged[k] = v;
                    fields = await keys.ValidateFieldsAsync(key.TenantId, merged, key.CustomFields);
                }
                key.Tags = newTags;
                key.CustomFields = fields;
                key.UpdatedAt = DateTimeOffset.UtcNow;
                audit.Add(AuditService.User(user), AuditActions.KeyUpdated, key.TenantId, key.Id, key.SerialNumber?.ToString(), new { before, after = new { key.Tags, key.CustomFields }, bulk = true });
                await db.SaveChangesAsync();
                break;
            }
            case BulkAction.Assign:
            {
                await cu.RequireAsync(Permission.ManagePartnerStock);
                var user = await cu.RequireTenantAsync(body.TenantId!.Value, Permission.ManageInventory);
                await keys.AssignToTenantAsync(key, body.TenantId.Value, AuditService.User(user));
                break;
            }
            case BulkAction.ToPartnerStock:
            {
                if (key.TenantId is null) throw ApiException.Conflict("already_partner_stock", L.T("Liegt bereits im Partnerlager."));
                await cu.RequireTenantAsync(key.TenantId.Value, Permission.ManageInventory);
                var user = await cu.RequireAsync(Permission.ManagePartnerStock);
                await keys.MoveToPartnerStockAsync(key, AuditService.User(user));
                break;
            }
            case BulkAction.Retire:
            {
                var user = await cu.RequireKeyAccessAsync(key.TenantId, Permission.RetireKeys);
                var toPartner = body.ToPartnerStock && key.TenantId is not null;
                if (toPartner) await cu.RequireAsync(Permission.ManagePartnerStock);
                await keys.RetireAsync(key, body.Note, AuditService.User(user), toPartner);
                break;
            }
        }
    }
}
