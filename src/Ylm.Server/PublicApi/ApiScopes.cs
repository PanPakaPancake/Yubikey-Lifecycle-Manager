using System.Security.Claims;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;

namespace Ylm.Server.PublicApi;

/// <summary>Beschreibung eines API-Rechts für die Schlüsselverwaltung und die OpenAPI-Doku.</summary>
public sealed record ApiScopeInfo(string Id, string Area, string Label, string Description, bool Write);

/// <summary>Endpunkt-Metadaten: welches Recht ein Endpunkt der API braucht.</summary>
public sealed record RequiresApiScope(string Scope);

/// <summary>
/// Rechte eines API-Schlüssels. Jeder Schlüssel hat eine Auswahl dieser Rechte, kombiniert mit seinem
/// Geltungsbereich (alle Tenants oder ausgewählte). So entstehen z. B. „nur lesen, alle Tenants“,
/// „nur lesen, Mandant 1“ oder „lesen und schreiben, alle Tenants“.
/// </summary>
public static class ApiScopes
{
    public const string InventoryRead = "inventory.read";
    public const string AssignmentsRead = "assignments.read";
    public const string ReportsRead = "reports.read";
    public const string TodosRead = "todos.read";
    public const string AuditRead = "audit.read";
    public const string InventoryWrite = "inventory.write";
    public const string TodosWrite = "todos.write";
    public const string KeysLock = "keys.lock";
    public const string CertificateWrite = "certificate.write";

    public static readonly IReadOnlyList<ApiScopeInfo> Catalog =
    [
        new(InventoryRead, "Inventar", "Inventar lesen", "Tenants, Keys mit Seriennummer, Firmware, Status, Tags, Zusatzfeldern und Registrierungen.", false),
        new(AssignmentsRead, "Inventar", "Zuweisungen lesen", "Wer hat welchen Key (Person, Verwahrer, Konten auf dem Key).", false),
        new(ReportsRead, "Berichte", "Kennzahlen lesen", "Kennzahlen wie Bestand nach Status und Abdeckung der Zielgruppe, dazu die Keys mit Yubico-Sicherheitshinweis.", false),
        new(TodosRead, "ToDo-Liste", "ToDo-Liste lesen", "Offene und erledigte ToDos (Personen, die einen Key bekommen sollen).", false),
        new(AuditRead, "Audit-Log", "Audit-Log lesen", "Einträge des Audit-Logs, z. B. für ein SIEM. Einträge ohne Tenant nur bei Schlüsseln für alle Tenants.", false),
        new(InventoryWrite, "Inventar", "Inventar pflegen", "Tags, Zusatzfelder und Notiz eines Keys ändern. Ändert nichts in Entra.", true),
        new(TodosWrite, "ToDo-Liste", "ToDo-Liste schreiben", "Personen auf die ToDo-Liste setzen und offene ToDos zurückziehen.", true),
        new(KeysLock, "Keys", "Keys sperren", "Key sperren, z. B. bei Verlust. Entfernt den Passkey der Person in Entra, die Person kann sich mit dem Key nicht mehr anmelden.", true),
        new(CertificateWrite, "Plattform", "HTTPS-Zertifikat tauschen", "Das eigene HTTPS-Zertifikat der Plattform lesen und durch ein neues ersetzen, z. B. automatisch aus der Firmen-PKI. Nur für Schlüssel mit allen Tenants.", true),
    ];

    public static IReadOnlyList<string> All => Catalog.Select(s => s.Id).ToList();

    public static IReadOnlyList<string> ReadOnly => Catalog.Where(s => !s.Write).Select(s => s.Id).ToList();

    /// <summary>Rechte bisheriger Schlüssel: alles Lesende, das es vor den Schlüsselrechten gab, plus ToDo schreiben, wenn erlaubt.</summary>
    public static List<string> Legacy(bool allowTodoWrite) =>
        allowTodoWrite ? [InventoryRead, AssignmentsRead, ReportsRead, TodosRead, TodosWrite] : [InventoryRead, AssignmentsRead, ReportsRead, TodosRead];

    public static List<string> Normalize(IEnumerable<string>? scopes)
    {
        var list = (scopes ?? []).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().ToList();
        var unknown = list.Where(s => Catalog.All(c => c.Id != s)).ToList();
        if (unknown.Count > 0) throw ApiException.BadRequest("invalid_scope", $"Unbekanntes Recht: {string.Join(", ", unknown)}.");
        if (list.Count == 0) throw ApiException.BadRequest("invalid_scope", "Bitte mindestens ein Recht auswählen.");
        // Feste Reihenfolge wie im Katalog, damit Anzeige und Audit-Log stabil bleiben.
        return Catalog.Select(c => c.Id).Where(list.Contains).ToList();
    }

    public static bool Has(ClaimsPrincipal p, string scope) => p.HasClaim(YlmClaims.ApiKeyScope, scope);

    public static void Require(ClaimsPrincipal p, string scope)
    {
        if (Has(p, scope)) return;
        var label = Catalog.FirstOrDefault(c => c.Id == scope)?.Label ?? scope;
        throw ApiException.Forbidden($"Diesem API-Schlüssel fehlt das Recht „{label}“ ({scope}). Rechte werden unter „Globale Einstellungen → API-Schlüssel“ vergeben.");
    }

    /// <summary>Audit-Akteur für Änderungen über die API: der Schlüssel mit Name und ID.</summary>
    public static AuditActor Actor(ClaimsPrincipal p) =>
        new(ActorType.Api, Guid.TryParse(p.FindFirstValue(YlmClaims.ApiKeyId), out var id) ? id : null, p.Identity?.Name);

    /// <summary>Endpunkt nur mit diesem Recht; erscheint auch in der OpenAPI-Doku.</summary>
    public static TBuilder RequireScope<TBuilder>(this TBuilder builder, string scope) where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new RequiresApiScope(scope));
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            Require(ctx.HttpContext.User, scope);
            return await next(ctx);
        });
        return builder;
    }
}
