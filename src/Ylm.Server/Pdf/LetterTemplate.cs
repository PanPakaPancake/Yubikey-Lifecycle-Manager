using System.Text.RegularExpressions;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Pdf;

/// <summary>
/// Platzhalter wie {{Name}} für den PIN-Brief. <see cref="Fields"/> sind die Einzeltexte des früheren Brief-Editors;
/// gespeicherte Werte werden beim Lesen in Bausteine übernommen (<see cref="LetterBlocks.FromLegacy"/>).
/// </summary>
public static partial class LetterTemplate
{
    public sealed record Field(string Key, string Label, string Default, bool Multiline = false, string? Hint = null);
    public sealed record Variable(string Name, string Description, string Example);

    public static IReadOnlyList<Field> Fields => _fields.Value;
    private static readonly L.PerLanguage<IReadOnlyList<Field>> _fields = new(() => [
        new("title", L.T("Überschrift"), L.T("Ihr YubiKey und Ihre PIN")),
        new("recipient", L.T("Empfängerzeile"), L.T("Persönlich für {{Name}} ({{Anmeldename}})")),
        new("intro", L.T("Einleitung"), L.T("Hallo {{Vorname}},\nanbei erhalten Sie Ihren YubiKey. Damit melden Sie sich künftig ohne Passwort an: Key einstecken, PIN eingeben, goldene Fläche berühren."), true, L.T("Ein einzelnes „-“ druckt keine Einleitung.")),
        new("pinForced", L.T("PIN-Hinweis (PIN-Wechsel erzwungen)"), L.T("Bei der ersten Verwendung werden Sie aufgefordert, eine neue, eigene PIN zu wählen."), true),
        new("pinOwn", L.T("PIN-Hinweis (PIN selbst ändern)"), L.T("Bitte ändern Sie Ihre PIN selbst, sobald Sie den Key erhalten haben (siehe unten)."), true),
        new("guideTitle", L.T("Überschrift der Anleitung"), L.T("So melden Sie sich an")),
        new("step1Title", L.T("Schritt 1: Titel"), L.T("Key einstecken")),
        new("step1Text", L.T("Schritt 1: Text"), L.T("Stecken Sie den YubiKey in einen freien USB-Anschluss Ihres Geräts."), true),
        new("step2Title", L.T("Schritt 2: Titel"), L.T("Option wählen")),
        new("step2Text", L.T("Schritt 2: Text"), L.T("Bei der Microsoft-Anmeldung „Gesicht, Fingerabdruck, PIN oder Sicherheitsschlüssel“ wählen."), true),
        new("step3Title", L.T("Schritt 3: Titel"), L.T("PIN eingeben")),
        new("step3Text", L.T("Schritt 3: Text"), L.T("Geben Sie Ihre PIN ein, wenn Windows oder der Browser danach fragt."), true),
        new("step4Title", L.T("Schritt 4: Titel"), L.T("Key berühren")),
        new("step4Text", L.T("Schritt 4: Text"), L.T("Berühren Sie die goldene Fläche, sobald der Key blinkt. Fertig."), true),
        new("changeTitle", L.T("Kasten „PIN selbst ändern“: Titel"), L.T("So ändern Sie Ihre PIN")),
        new("changeText", L.T("Kasten „PIN selbst ändern“: Text"), L.T("Windows: Einstellungen → Konten → Anmeldeoptionen → Sicherheitsschlüssel → Verwalten → Sicherheitsschlüssel-PIN „Ändern“. Wählen Sie eine PIN, die nur Sie kennen."), true),
        new("hintsTitle", L.T("Überschrift der Hinweise"), L.T("Wichtig")),
        new("hints", L.T("Hinweise (eine Zeile pro Punkt)"), string.Join("\n",
            L.T("Geben Sie Ihre PIN niemals weiter. Die IT wird Sie nie danach fragen."),
            L.T("Nach 8 Fehleingaben wird der Key gesperrt. Wenden Sie sich dann an die IT{{KontaktMitDoppelpunkt}}."),
            L.T("Key verloren oder gestohlen? Melden Sie es sofort der IT, damit er gesperrt wird."),
            L.T("Bewahren Sie diesen Brief nicht zusammen mit dem Key auf und vernichten Sie ihn, sobald Sie die PIN geändert haben.")), true),
        new("infoTitle", L.T("Hinweis auf die Infoseite"), L.T("Was ist ein Passkey? Anleitung und Erklärung:")),
        new("closing", L.T("Grußzeile"), L.T("Bei Fragen hilft Ihnen Ihre IT gern weiter."), true, L.T("Ein einzelnes „-“ druckt keine Grußzeile.")),
    ]);

    public static IReadOnlyList<Variable> Variables => _variables.Value;
    private static readonly L.PerLanguage<IReadOnlyList<Variable>> _variables = new(() => [
        new("Name", L.T("Vollständiger Name"), L.T("Erika Mustermann")),
        new("Vorname", L.T("Vorname"), L.T("Erika")),
        new("Anmeldename", L.T("Anmeldename (UPN)"), L.T("erika.mustermann@example.com")),
        new("Firma", L.T("Firmenname aus dem Erscheinungsbild"), L.T("Muster IT GmbH")),
        new("Tenant", L.T("Name des Tenants"), L.T("Contoso GmbH")),
        new("Modell", L.T("Modell des Keys"), "YubiKey 5 NFC"),
        new("Seriennummer", L.T("Seriennummer des Keys"), "12345678"),
        new("Datum", L.T("Erstellungsdatum"), "20.11.2026"),
        new("Kontakt", L.T("Kontakt für Rückfragen"), L.T("IT-Service · 0800 123 456")),
        new("KontaktMitDoppelpunkt", L.T("„: Kontakt“, leer ohne Kontakt"), L.T(": IT-Service · 0800 123 456")),
    ]);

    /// <summary>Ein einzelnes „-“ heißt: diesen Teil weglassen (leer heißt dagegen: Vorgabe übernehmen).</summary>
    public static bool IsOmitted(string text) => text.Trim() == "-";

    public static string Default(string key) => Fields.First(f => f.Key == key).Default;

    /// <summary>Wirksamer Text: erster gesetzter Wert aus den Ebenen (z. B. Tenant, Partner), sonst Standardtext.</summary>
    public static string Resolve(string key, params IReadOnlyDictionary<string, string>?[] layers)
    {
        foreach (var l in layers)
            if (l is not null && l.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return Default(key);
    }

    /// <summary>Setzt die Werte für {{Platzhalter}} ein; unbekannte Platzhalter bleiben sichtbar stehen.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string?> values) =>
        PlaceholderRegex().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v ?? "" : m.Value);

    [GeneratedRegex(@"\{\{\s*([A-Za-zÄÖÜäöüß]+)\s*\}\}")]
    private static partial Regex PlaceholderRegex();
}
