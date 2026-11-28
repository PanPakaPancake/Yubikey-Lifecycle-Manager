using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Reporting;

/// <summary>Seite „Erinnerungen“: offene Lebenszyklus-Erinnerungen ansehen, ausblenden und wieder einblenden.</summary>
public static class ReminderEndpoints
{
    public sealed record DismissRequest(Guid TenantId, ReminderKind Kind, string Subject, string? Note);

    public static void MapReminders(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/reminders").RequireAuthorization();

        g.MapGet("", async (Guid? tenantId, bool? includeDismissed, CurrentUser cu, ReminderService reminders) =>
        {
            await cu.RequireAsync(Permission.ReadInventory);
            var ids = await cu.AccessibleTenantIdsAsync();
            if (tenantId is { } t)
            {
                if (!ids.Contains(t)) throw ApiException.Forbidden(L.T("Sie sind für diesen Tenant nicht zuständig."));
                ids = [t];
            }
            return Results.Ok(await reminders.BuildAsync(ids, includeDismissed == true));
        });

        g.MapPost("/dismiss", (DismissRequest body, CurrentUser cu, ReminderService reminders) => SetAsync(body, true, cu, reminders));
        g.MapPost("/restore", (DismissRequest body, CurrentUser cu, ReminderService reminders) => SetAsync(body, false, cu, reminders));
    }

    private static async Task<IResult> SetAsync(DismissRequest body, bool dismissed, CurrentUser cu, ReminderService reminders)
    {
        // Ausblenden ändert nichts in Entra, betrifft aber, was das Team als offen sieht: wer das Inventar pflegt.
        var user = await cu.RequireTenantAsync(body.TenantId, Permission.ManageInventory);
        if (string.IsNullOrWhiteSpace(body.Subject) || body.Subject.Length > 200) throw ApiException.BadRequest("invalid", L.T("Unbekannte Erinnerung."));
        if (!Enum.IsDefined(body.Kind)) throw ApiException.BadRequest("invalid", L.T("Unbekannte Art der Erinnerung."));
        await reminders.SetDismissedAsync(body.TenantId, body.Kind, body.Subject.Trim(), dismissed, body.Note, AuditService.User(user));
        return Results.NoContent();
    }
}
