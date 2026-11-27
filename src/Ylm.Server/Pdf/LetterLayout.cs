using System.Text.Json;
using System.Text.Json.Serialization;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Pdf;

/// <summary>
/// Aufbau des PIN-Briefs als Folge von Bausteinen (Widgets), die im Brief-Editor gezogen, verschoben und befüllt werden.
/// Zwei aufeinanderfolgende Bausteine mit halber Breite stehen nebeneinander.
/// </summary>
public sealed class LetterLayout
{
    public int Version { get; set; } = 1;
    public List<LetterBlock> Blocks { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static LetterLayout? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<LetterLayout>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    public LetterLayout Clone() => FromJson(ToJson())!;
}

public sealed class LetterBlock
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    /// <summary>"full" oder "half".</summary>
    public string Width { get; set; } = "full";
    /// <summary>"always", "forced" (nur mit Initial-PIN, PIN-Wechsel erzwungen) oder "own" (nur ohne erzwungenen Wechsel).</summary>
    public string Show { get; set; } = "always";
    public Dictionary<string, string> Props { get; set; } = [];

    public string P(string key) => Props.TryGetValue(key, out var v) && v is not null ? v : LetterBlocks.DefaultProp(Type, key);

    public bool VisibleFor(bool pinChangeForced) => Show switch
    {
        "forced" => pinChangeForced,
        "own" => !pinChangeForced,
        _ => true,
    };
}

/// <summary>Bausteine des Brief-Editors mit ihren Eigenschaften und Vorgabetexten.</summary>
public static class LetterBlocks
{
    public sealed record Option(string Value, string Label);
    /// <summary>Kind: text, multiline, select, toggle.</summary>
    public sealed record Prop(string Key, string Label, string Default, string Kind = "text", IReadOnlyList<Option>? Options = null, string? Hint = null);
    public sealed record BlockType(string Type, string Label, string Description, IReadOnlyList<Prop> Props, bool HalfWidth = true, bool Unique = false, bool Required = false);

    public const int MaxBlocks = 80;
    /// <summary>Höchstzahl der Seiten, die mit „Neue Seite“ angelegt werden können.</summary>
    public const int MaxPages = 6;
    public const int MaxLength = 1500;

    public const string DefaultPinChange =
        "Windows: Einstellungen → Konten → Anmeldeoptionen → Sicherheitsschlüssel → „Verwalten“ → Sicherheitsschlüssel-PIN „Ändern“.\n" +
        "Mac: In Chrome chrome://settings/securityKeys öffnen → „PIN erstellen oder ändern“.\n" +
        "Oder: Yubico Authenticator → Passkeys → PIN ändern.\n" +
        "Tipp: Sie brauchen dazu Ihre aktuelle PIN. Wählen Sie 6 bis 8 Ziffern, die nur Sie kennen.";

    private static readonly IReadOnlyList<Option> Sizes = [new("l", "Groß"), new("m", "Mittel"), new("s", "Klein")];
    private static readonly IReadOnlyList<Option> Tones = [new("accent", "Akzentfarbe"), new("warn", "Warnung (orange)"), new("neutral", "Neutral (grau)")];
    private static readonly IReadOnlyList<Option> Spaces = [new("s", "Klein"), new("m", "Mittel"), new("l", "Groß")];

    public static readonly IReadOnlyList<BlockType> Types =
    [
        new("header", "Kopfzeile", "Firmenname und Logo aus dem Erscheinungsbild.", [], HalfWidth: false),
        new("heading", "Überschrift", "Titel oder Zwischenüberschrift.",
        [
            new("text", "Text", "Ihr YubiKey und Ihre PIN"),
            new("size", "Größe", "l", "select", Sizes),
        ]),
        new("recipient", "Empfänger", "Für wen der Brief ist, darunter Modell und Seriennummer des Keys.",
        [
            new("text", "Empfängerzeile", "Persönlich für {{Name}} ({{Anmeldename}})", Hint: "Bei geteilten Keys steht hier automatisch der Verwahrer mit den Konten."),
            new("keyLine", "Key-Daten anzeigen", "true", "toggle"),
        ]),
        new("text", "Text", "Freier Absatz mit Platzhaltern.",
        [
            new("text", "Text", "Hallo {{Vorname}},\nanbei erhalten Sie Ihren YubiKey.", "multiline"),
            new("size", "Größe", "m", "select", [new("m", "Normal"), new("s", "Klein")]),
        ]),
        new("pin", "PIN", "Die PIN in großen Ziffern. Bei erzwungenem PIN-Wechsel deutlich als Initial-PIN gekennzeichnet.",
        [
            new("forcedLabel", "Bezeichnung (Initial-PIN)", "Ihre Initial-PIN"),
            new("forcedBadge", "Kennzeichnung (Initial-PIN)", "Nur für die erste Anmeldung"),
            new("forcedText", "Hinweis (Initial-PIN)", "Diese PIN gilt nur für die erste Anmeldung. Danach werden Sie aufgefordert, eine neue, eigene PIN zu wählen. Ab dann ist diese Initial-PIN ungültig.", "multiline"),
            new("ownLabel", "Bezeichnung (dauerhafte PIN)", "Ihre persönliche PIN"),
            new("ownText", "Hinweis (dauerhafte PIN)", "Bitte ändern Sie Ihre PIN selbst, sobald Sie den Key erhalten haben. Wie das geht, steht unter „PIN später selbst ändern“.", "multiline"),
        ], Unique: true, Required: true),
        new("steps", "Anleitung", "Vier bebilderte Schritte zur Anmeldung.",
        [
            new("title", "Überschrift", "So melden Sie sich an"),
            new("step1Title", "Schritt 1: Titel", "Key einstecken"),
            new("step1Text", "Schritt 1: Text", "Stecken Sie den YubiKey in einen freien USB-Anschluss Ihres Geräts.", "multiline"),
            new("step2Title", "Schritt 2: Titel", "Option wählen"),
            new("step2Text", "Schritt 2: Text", "Bei der Anmeldung „Gesicht, Fingerabdruck, PIN oder Sicherheitsschlüssel“ wählen.", "multiline"),
            new("step3Title", "Schritt 3: Titel", "PIN eingeben"),
            new("step3Text", "Schritt 3: Text", "Geben Sie Ihre PIN ein, wenn Windows oder der Browser danach fragt.", "multiline"),
            new("step4Title", "Schritt 4: Titel", "Key berühren"),
            new("step4Text", "Schritt 4: Text", "Berühren Sie die goldene Fläche, sobald der Key blinkt. Fertig.", "multiline"),
        ], HalfWidth: false),
        new("pinChange", "PIN ändern", "Anleitung, wie man die PIN später selbst ändert.",
        [
            new("title", "Überschrift", "PIN später selbst ändern"),
            new("text", "Anleitung (eine Zeile pro Punkt)", DefaultPinChange, "multiline", Hint: "Text vor einem Doppelpunkt wird fett gedruckt."),
        ]),
        new("hints", "Hinweisliste", "Liste mit Haken, ein Punkt pro Zeile.",
        [
            new("title", "Überschrift", "Wichtig"),
            new("items", "Punkte (eine Zeile pro Punkt)",
                "Geben Sie Ihre PIN niemals weiter. Die IT wird Sie nie danach fragen.\n" +
                "Nach 8 Fehleingaben wird der Key gesperrt. Wenden Sie sich dann an die IT{{KontaktMitDoppelpunkt}}.\n" +
                "Key verloren oder gestohlen? Melden Sie es sofort der IT, damit er gesperrt wird.\n" +
                "Bewahren Sie diesen Brief nicht zusammen mit dem Key auf und vernichten Sie ihn, sobald Sie die PIN geändert haben.", "multiline"),
        ]),
        new("callout", "Hinweiskasten", "Hervorgehobener Kasten mit Titel und Text.",
        [
            new("title", "Titel", "Gut zu wissen"),
            new("text", "Text", "Ihr YubiKey funktioniert an jedem Gerät mit USB-Anschluss.", "multiline"),
            new("tone", "Farbe", "accent", "select", Tones),
        ]),
        new("info", "Infoseite", "Link zur Passkey-Infoseite. Erscheint nur, wenn die Adresse der Plattform bekannt ist.",
        [
            new("title", "Text", "Was ist ein Passkey? Anleitung und Erklärung:"),
        ]),
        new("divider", "Trennlinie", "Feine Linie mit Akzent.", [], HalfWidth: false),
        new("pageBreak", "Neue Seite", "Seitenumbruch: alles darunter steht auf der nächsten Seite.", [], HalfWidth: false),
        new("spacer", "Abstand", "Freier Raum zwischen Bausteinen.",
        [
            new("size", "Höhe", "m", "select", Spaces),
        ], HalfWidth: false),
    ];

    private static readonly Dictionary<string, BlockType> ByType = Types.ToDictionary(t => t.Type);

    public static BlockType? Find(string type) => ByType.GetValueOrDefault(type);

    public static string DefaultProp(string type, string key) =>
        Find(type)?.Props.FirstOrDefault(p => p.Key == key)?.Default ?? "";

    private static LetterBlock Block(string id, string type, Dictionary<string, string>? props = null, string show = "always") =>
        new() { Id = id, Type = type, Show = show, Props = props ?? [] };

    /// <summary>Standardaufbau des Briefs.</summary>
    public static LetterLayout Default() => FromLegacy(null, null);

    /// <summary>
    /// Aufbau aus den Einzeltexten des früheren Brief-Editors (Tenant vor Partner vor Standard), damit angepasste Texte erhalten bleiben.
    /// </summary>
    public static LetterLayout FromLegacy(IReadOnlyDictionary<string, string>? tenant, IReadOnlyDictionary<string, string>? partner)
    {
        string? Custom(string key)
        {
            foreach (var l in new[] { tenant, partner })
                if (l is not null && l.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            return null;
        }
        Dictionary<string, string> Map(params (string Prop, string Legacy)[] pairs)
        {
            var d = new Dictionary<string, string>();
            foreach (var (prop, legacy) in pairs)
                if (Custom(legacy) is { } v) d[prop] = v;
            return d;
        }

        var blocks = new List<LetterBlock>
        {
            Block("kopf", "header"),
            Block("titel", "heading", Map(("text", "title"))),
            Block("empfaenger", "recipient", Map(("text", "recipient"))),
            Block("linie", "divider"),
        };
        var intro = Custom("intro") ?? "Hallo {{Vorname}},\nanbei erhalten Sie Ihren YubiKey. Damit melden Sie sich künftig ohne Passwort an: Key einstecken, PIN eingeben, goldene Fläche berühren.";
        if (!LetterTemplate.IsOmitted(intro)) blocks.Add(Block("einleitung", "text", new() { ["text"] = intro }));
        blocks.Add(Block("pin", "pin", Map(("forcedText", "pinForced"), ("ownText", "pinOwn"))));
        blocks.Add(Block("anleitung", "steps", Map(("title", "guideTitle"),
            ("step1Title", "step1Title"), ("step1Text", "step1Text"), ("step2Title", "step2Title"), ("step2Text", "step2Text"),
            ("step3Title", "step3Title"), ("step3Text", "step3Text"), ("step4Title", "step4Title"), ("step4Text", "step4Text"))));
        // „PIN ändern“ und Hinweise stehen nebeneinander (je halbe Breite).
        var change = Block("pin-aendern", "pinChange", Map(("title", "changeTitle"), ("text", "changeText")));
        var hints = Block("hinweise", "hints", Map(("title", "hintsTitle"), ("items", "hints")));
        change.Width = hints.Width = "half";
        blocks.Add(change);
        blocks.Add(hints);
        blocks.Add(Block("infoseite", "info", Map(("title", "infoTitle"))));
        var closing = Custom("closing") ?? "Bei Fragen hilft Ihnen Ihre IT gern weiter.";
        if (!LetterTemplate.IsOmitted(closing)) blocks.Add(Block("gruss", "text", new() { ["text"] = closing }));
        return new LetterLayout { Blocks = blocks };
    }

    /// <summary>Prüft einen Aufbau aus dem Editor: bekannte Bausteine und Eigenschaften, genau eine PIN, Längen begrenzt.</summary>
    public static LetterLayout Validate(LetterLayout? input)
    {
        if (input?.Blocks is not { } blocks) throw ApiException.BadRequest("invalid_layout", "Der Briefaufbau fehlt.");
        if (blocks.Count > MaxBlocks) throw ApiException.BadRequest("too_many_blocks", $"Höchstens {MaxBlocks} Bausteine pro Brief.");
        var result = new LetterLayout();
        var ids = new HashSet<string>();
        foreach (var b in blocks)
        {
            var type = Find(b?.Type ?? "") ?? throw ApiException.BadRequest("invalid_block", $"Unbekannter Baustein „{b?.Type}“.");
            var id = string.IsNullOrWhiteSpace(b!.Id) || b.Id.Length > 40 ? Guid.NewGuid().ToString("N")[..8] : b.Id.Trim();
            while (!ids.Add(id)) id = Guid.NewGuid().ToString("N")[..8];
            var props = new Dictionary<string, string>();
            foreach (var p in type.Props)
            {
                if (b.Props is null || !b.Props.TryGetValue(p.Key, out var v) || v is null) continue;
                v = v.Replace("\r\n", "\n");
                if (v.Length > MaxLength) throw ApiException.BadRequest("too_long", $"„{type.Label}: {p.Label}“ ist zu lang (höchstens {MaxLength} Zeichen).");
                if (p.Options is { } opts && opts.All(o => o.Value != v)) continue;
                if (p.Kind == "toggle" && v is not ("true" or "false")) continue;
                props[p.Key] = v;
            }
            result.Blocks.Add(new LetterBlock
            {
                Id = id,
                Type = type.Type,
                Width = b.Width == "half" && type.HalfWidth ? "half" : "full",
                Show = b.Show is "forced" or "own" ? b.Show : "always",
                Props = props,
            });
        }
        foreach (var t in Types.Where(t => t.Unique))
            if (result.Blocks.Count(b => b.Type == t.Type) > 1)
                throw ApiException.BadRequest("duplicate_block", $"Der Baustein „{t.Label}“ darf nur einmal im Brief stehen.");
        foreach (var t in Types.Where(t => t.Required))
            if (result.Blocks.All(b => b.Type != t.Type))
                throw ApiException.BadRequest("missing_block", $"Der Baustein „{t.Label}“ muss im Brief stehen.");
        if (result.Blocks.Count(b => b.Type == "pageBreak") > MaxPages - 1)
            throw ApiException.BadRequest("too_many_pages", $"Höchstens {MaxPages} Seiten pro Brief.");
        if (result.Blocks.First(b => b.Type == "pin").Show != "always")
            throw ApiException.BadRequest("pin_hidden", "Der Baustein „PIN“ muss immer angezeigt werden.");
        return result;
    }
}
