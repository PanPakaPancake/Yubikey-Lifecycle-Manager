using System.Text;

namespace Ylm.Core;

/// <summary>CSV writer tuned for German Excel (semicolon, UTF-8 BOM) with formula-injection protection.</summary>
public static class Csv
{
    public const char Separator = ';';

    public static byte[] Write(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        AppendRow(sb, header);
        foreach (var row in rows) AppendRow(sb, row);
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        return [.. preamble, .. body];
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string?> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(Separator);
            sb.Append(Escape(values[i]));
        }
        sb.Append("\r\n");
    }

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // Prevent CSV/formula injection in spreadsheet programs.
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        if (value.IndexOfAny([Separator, '"', '\n', '\r', ',']) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    /// <summary>Minimal CSV reader (semicolon or comma) for stock imports.</summary>
    public static List<string[]> Read(string text)
    {
        var lines = new List<string[]>();
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var firstLine = text.Split('\n', 2)[0];
        var sep = firstLine.Count(c => c == ';') >= firstLine.Count(c => c == ',') ? ';' : ',';
        var field = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
                continue;
            }
            if (c == '"') inQuotes = true;
            else if (c == sep) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n')
            {
                row.Add(field.ToString().TrimEnd('\r')); field.Clear();
                if (row.Count > 1 || row[0].Length > 0) lines.Add([.. row]);
                row = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString().TrimEnd('\r'));
            if (row.Count > 1 || row[0].Length > 0) lines.Add([.. row]);
        }
        return lines;
    }
}
