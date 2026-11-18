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

/// <summary>Where a custom field was created: on platform level (inherited by every tenant) or in one tenant.</summary>
public enum CustomFieldOrigin { Global = 0, Tenant = 1 }

/// <summary>How a field applies in a tenant.</summary>
public enum CustomFieldState
{
    /// <summary>Global field, used unchanged.</summary>
    Inherited = 0,
    /// <summary>Global field, label, required flag or options changed for this tenant.</summary>
    Modified = 1,
    /// <summary>Global field, switched off for this tenant (inheritance interrupted).</summary>
    Disabled = 2,
    /// <summary>Field created in this tenant.</summary>
    Local = 3,
}

public sealed record CustomFieldDef(Guid Id, CustomFieldSpec Spec, int SortOrder);

/// <summary>Tenant-level change of a global field. Key and type cannot be changed, otherwise API and exports would no longer match.</summary>
public sealed record CustomFieldOverrideSpec(bool Disabled, string? Label, bool? Required, IReadOnlyList<string>? Options);

public sealed record ResolvedCustomField(Guid Id, CustomFieldSpec Spec, CustomFieldOrigin Origin, CustomFieldState State, CustomFieldSpec? Inherited, int SortOrder)
{
    public bool IsActive => State != CustomFieldState.Disabled;
}

/// <summary>Platform → tenant inheritance of custom fields.</summary>
public static class CustomFieldInheritance
{
    /// <summary>
    /// Effective field list of a tenant: global fields first (with the tenant's overrides applied, disabled ones kept
    /// with state Disabled so the UI can show them), then the tenant's own fields.
    /// </summary>
    public static IReadOnlyList<ResolvedCustomField> Resolve(
        IEnumerable<CustomFieldDef> global, IReadOnlyDictionary<Guid, CustomFieldOverrideSpec> overrides, IEnumerable<CustomFieldDef> local)
    {
        var result = new List<ResolvedCustomField>();
        foreach (var g in global.OrderBy(f => f.SortOrder))
        {
            if (!overrides.TryGetValue(g.Id, out var o))
            {
                result.Add(new(g.Id, g.Spec, CustomFieldOrigin.Global, CustomFieldState.Inherited, g.Spec, g.SortOrder));
                continue;
            }
            var spec = Apply(g.Spec, o);
            var state = o.Disabled ? CustomFieldState.Disabled : spec == g.Spec || SameSpec(spec, g.Spec) ? CustomFieldState.Inherited : CustomFieldState.Modified;
            result.Add(new(g.Id, spec, CustomFieldOrigin.Global, state, g.Spec, g.SortOrder));
        }
        result.AddRange(local.OrderBy(f => f.SortOrder)
            .Select(l => new ResolvedCustomField(l.Id, l.Spec, CustomFieldOrigin.Tenant, CustomFieldState.Local, null, l.SortOrder)));
        return result;
    }

    public static CustomFieldSpec Apply(CustomFieldSpec global, CustomFieldOverrideSpec o) => global with
    {
        Label = string.IsNullOrWhiteSpace(o.Label) ? global.Label : o.Label.Trim(),
        Required = o.Required ?? global.Required,
        Options = global.Type == CustomFieldType.Select && o.Options is { Count: > 0 } opts ? opts : global.Options,
    };

    /// <summary>True when the override changes nothing (then it is not stored).</summary>
    public static bool IsNoOp(CustomFieldSpec global, CustomFieldOverrideSpec o) => !o.Disabled && SameSpec(Apply(global, o), global);

    private static bool SameSpec(CustomFieldSpec a, CustomFieldSpec b) =>
        a.Key == b.Key && a.Label == b.Label && a.Type == b.Type && a.Required == b.Required && a.Options.SequenceEqual(b.Options);
}
