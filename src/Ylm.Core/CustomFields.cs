using System.Globalization;

namespace Ylm.Core;

public enum CustomFieldType { Text = 0, Number = 1, Date = 2, Select = 3, Boolean = 4 }

public sealed record CustomFieldSpec(string Key, string Label, CustomFieldType Type, bool Required, IReadOnlyList<string> Options);

public static class CustomFieldValidator
{
    public static IReadOnlyList<string> Validate(IReadOnlyList<CustomFieldSpec> specs, IReadOnlyDictionary<string, string?> values)
    {
        var errors = new List<string>();
        var byKey = specs.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var key in values.Keys.Where(k => !byKey.ContainsKey(k)))
            errors.Add($"Unbekanntes Feld „{key}“.");

        foreach (var spec in specs)
        {
            values.TryGetValue(spec.Key, out var raw);
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (spec.Required) errors.Add($"„{spec.Label}“ ist ein Pflichtfeld.");
                continue;
            }
            switch (spec.Type)
            {
                case CustomFieldType.Number when !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)
                                                 && !decimal.TryParse(value, NumberStyles.Number, CultureInfo.GetCultureInfo("de-DE"), out _):
                    errors.Add($"„{spec.Label}“ muss eine Zahl sein.");
                    break;
                case CustomFieldType.Date when !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                    errors.Add($"„{spec.Label}“ muss ein Datum im Format JJJJ-MM-TT sein.");
                    break;
                case CustomFieldType.Select when !spec.Options.Contains(value, StringComparer.OrdinalIgnoreCase):
                    errors.Add($"„{spec.Label}“ muss einer der Werte {string.Join(", ", spec.Options)} sein.");
                    break;
                case CustomFieldType.Boolean when value is not ("true" or "false"):
                    errors.Add($"„{spec.Label}“ muss true oder false sein.");
                    break;
            }
        }
        return errors;
    }

    public static bool IsValidKey(string key) =>
        key.Length is > 0 and <= 40 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}

public static class Tags
{
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? tags) =>
        (tags ?? []).Select(t => t.Trim()).Where(t => t.Length is > 0 and <= 50)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
}
