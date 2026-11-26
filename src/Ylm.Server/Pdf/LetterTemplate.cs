using System.Text.RegularExpressions;

namespace Ylm.Server.Pdf;

/// <summary>
/// Platzhalter wie {{Name}} für den PIN-Brief. <see cref="Fields"/> sind die Einzeltexte des früheren Brief-Editors;
/// gespeicherte Werte werden beim Lesen in Bausteine übernommen (<see cref="LetterBlocks.FromLegacy"/>).
/// </summary>
public static partial class LetterTemplate
{
    public sealed record Field(string Key, string Label, string Default, bool Multiline = false, string? Hint = null);
    public sealed record Variable(string Name, string Description, string Example);

    public static readonly IReadOnlyList<Field> Fields =
    [
        new("title", "Überschrift", "Ihr YubiKey und Ihre PIN"),
        new("recipient", "Empfängerzeile", "Persönlich für {{Name}} ({{Anmeldename}})"),
        new("intro", "Einleitung", "Hallo {{Vorname}},\nanbei erhalten Sie Ihren YubiKey. Damit melden Sie sich künftig ohne Passwort an: Key einstecken, PIN eingeben, goldene Fläche berühren.", true, "Ein einzelnes „-“ druckt keine Einleitung."),
        new("pinForced", "PIN-Hinweis (PIN-Wechsel erzwungen)", "Bei der ersten Verwendung werden Sie aufgefordert, eine neue, eigene PIN zu wählen.", true),
        new("pinOwn", "PIN-Hinweis (PIN selbst ändern)", "Bitte ändern Sie Ihre PIN selbst, sobald Sie den Key erhalten haben (siehe unten).", true),
        new("guideTitle", "Überschrift der Anleitung", "So melden Sie sich an"),
        new("step1Title", "Schritt 1: Titel", "Key einstecken"),
        new("step1Text", "Schritt 1: Text", "Stecken Sie den YubiKey in einen freien USB-Anschluss Ihres Geräts.", true),
        new("step2Title", "Schritt 2: Titel", "Option wählen"),
        new("step2Text", "Schritt 2: Text", "Bei der Microsoft-Anmeldung „Gesicht, Fingerabdruck, PIN oder Sicherheitsschlüssel“ wählen.", true),
        new("step3Title", "Schritt 3: Titel", "PIN eingeben"),
        new("step3Text", "Schritt 3: Text", "Geben Sie Ihre PIN ein, wenn Windows oder der Browser danach fragt.", true),
        new("step4Title", "Schritt 4: Titel", "Key berühren"),
        new("step4Text", "Schritt 4: Text", "Berühren Sie die goldene Fläche, sobald der Key blinkt. Fertig.", true),
        new("changeTitle", "Kasten „PIN selbst ändern“: Titel", "So ändern Sie Ihre PIN"),
        new("changeText", "Kasten „PIN selbst ändern“: Text", "Windows: Einstellungen → Konten → Anmeldeoptionen → Sicherheitsschlüssel → Verwalten → Sicherheitsschlüssel-PIN „Ändern“. Wählen Sie eine PIN, die nur Sie kennen.", true),
        new("hintsTitle", "Überschrift der Hinweise", "Wichtig"),
        new("hints", "Hinweise (eine Zeile pro Punkt)",
            "Geben Sie Ihre PIN niemals weiter. Die IT wird Sie nie danach fragen.\n" +
            "Nach 8 Fehleingaben wird der Key gesperrt. Wenden Sie sich dann an die IT{{KontaktMitDoppelpunkt}}.\n" +
            "Key verloren oder gestohlen? Melden Sie es sofort der IT, damit er gesperrt wird.\n" +
            "Bewahren Sie diesen Brief nicht zusammen mit dem Key auf und vernichten Sie ihn, sobald Sie die PIN geändert haben.", true),
        new("infoTitle", "Hinweis auf die Infoseite", "Was ist ein Passkey? Anleitung und Erklärung:"),
        new("closing", "Grußzeile", "Bei Fragen hilft Ihnen Ihre IT gern weiter.", true, "Ein einzelnes „-“ druckt keine Grußzeile."),
    ];

    public static readonly IReadOnlyList<Variable> Variables =
    [
        new("Name", "Vollständiger Name", "Erika Mustermann"),
        new("Vorname", "Vorname", "Erika"),
        new("Anmeldename", "Anmeldename (UPN)", "erika.mustermann@example.com"),
        new("Firma", "Firmenname aus dem Erscheinungsbild", "Muster IT GmbH"),
        new("Tenant", "Name des Tenants", "Contoso GmbH"),
        new("Modell", "Modell des Keys", "YubiKey 5 NFC"),
        new("Seriennummer", "Seriennummer des Keys", "12345678"),
        new("Datum", "Erstellungsdatum", "20.11.2026"),
        new("Kontakt", "Kontakt für Rückfragen", "IT-Service · 0800 123 456"),
        new("KontaktMitDoppelpunkt", "„: Kontakt“, leer ohne Kontakt", ": IT-Service · 0800 123 456"),
    ];

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
