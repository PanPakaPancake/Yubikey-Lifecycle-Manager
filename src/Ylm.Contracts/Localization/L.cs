using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Ylm.Contracts.Localization;

/// <summary>
/// Übersetzung der Texte von Server und Agent. Wie in der Weboberfläche ist der deutsche Text Quelle und Schlüssel,
/// die englische Fassung steht in <c>Localization/en.json</c> (Schlüssel = deutscher Text, bei <see cref="F"/> mit {0}, {1} …).
/// Fehlt eine Übersetzung, bleibt der deutsche Text stehen. <c>node tools/i18n-check-server.mjs</c> prüft die Datei.
/// Die Sprache gilt je Ablauf (AsyncLocal): pro Anfrage aus Kopfzeile/Cookie, für Mails und Hintergrunddienste über <see cref="Use"/>.
/// </summary>
public static class L
{
    public const string German = "de";
    public const string English = "en";
    public static readonly IReadOnlyList<string> Languages = [German, English];

    private static readonly AsyncLocal<string?> Current = new();
    private static readonly Lazy<IReadOnlyDictionary<string, string>> En = new(LoadEnglish);
    private static readonly CultureInfo DeCulture = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>Aktuelle Sprache, "de" oder "en".</summary>
    public static string Language => Current.Value ?? German;
    public static bool IsEnglish => Language == English;
    public static CultureInfo Culture => IsEnglish ? EnCulture : DeCulture;

    /// <summary>"en", "en-US" usw. → "en", alles andere (auch leer) → null.</summary>
    public static string? Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        var l = language.Trim().ToLowerInvariant();
        if (l.StartsWith("en", StringComparison.Ordinal)) return English;
        if (l.StartsWith("de", StringComparison.Ordinal)) return German;
        return null;
    }

    /// <summary>Setzt die Sprache für den laufenden Ablauf, bis das Ergebnis verworfen wird.</summary>
    public static IDisposable Use(string? language)
    {
        var previous = Current.Value;
        Current.Value = Normalize(language) ?? German;
        return new Restore(previous);
    }

    /// <summary>Übersetzt einen festen Text.</summary>
    public static string T(string german) => IsEnglish && En.Value.TryGetValue(german, out var en) && en.Length > 0 ? en : german;

    /// <summary>Übersetzt einen interpolierten Text; Schlüssel ist der Text mit {0}, {1} … an Stelle der Werte.</summary>
    public static string F(FormattableString text) => string.Format(Culture, T(text.Format), text.GetArguments());

    /// <summary>Markiert einen Text für die Übersetzung, ohne ihn zu übersetzen (für feste Tabellen; übersetzt wird beim Anzeigen mit <see cref="T"/>).</summary>
    public static string N(string german) => german;

    /// <summary>Für Tests: alle englischen Einträge.</summary>
    public static IReadOnlyDictionary<string, string> EnglishEntries => En.Value;

    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using var s = typeof(L).Assembly.GetManifestResourceStream("Ylm.Contracts.Localization.en.json");
        if (s is null) return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? [];
    }

    /// <summary>
    /// Feste Tabelle mit übersetzten Texten (Rollen, Bausteine, Ereignisse …): wird je Sprache einmal aufgebaut,
    /// der Aufbau läuft dabei in dieser Sprache, sodass <see cref="T"/> darin wirkt.
    /// </summary>
    public sealed class PerLanguage<TValue>(Func<TValue> build)
    {
        private readonly Lazy<TValue> _de = new(() => { using (Use(German)) return build(); });
        private readonly Lazy<TValue> _en = new(() => { using (Use(English)) return build(); });
        public TValue Value => IsEnglish ? _en.Value : _de.Value;
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
