using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Ylm.Core;

/// <summary>
/// Minimal Excel writer (Office Open XML, one sheet) without external libraries:
/// bold header row, frozen first row, auto filter and column widths.
/// Cells hold strings (inline) or numbers; null stays empty. Values are never written as formulas.
/// </summary>
public static class Xlsx
{
    public static byte[] Write(string sheetName, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<object?>> rows)
    {
        var data = rows.ToList();
        var widths = header.Select(h => Math.Max(8, h.Length + 2)).ToArray();
        foreach (var r in data)
            for (var c = 0; c < Math.Min(r.Count, widths.Length); c++)
                widths[c] = Math.Min(60, Math.Max(widths[c], Text(r[c]).Length + 2));

        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sheet.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        sheet.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
        sheet.Append("<cols>");
        for (var c = 0; c < widths.Length; c++)
            sheet.Append(CultureInfo.InvariantCulture, $"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{widths[c]}\" customWidth=\"1\"/>");
        sheet.Append("</cols><sheetData>");
        AppendRow(sheet, 1, header.Cast<object?>().ToList(), style: 1);
        for (var i = 0; i < data.Count; i++) AppendRow(sheet, i + 2, data[i], style: 0);
        sheet.Append("</sheetData>");
        if (header.Count > 0)
            sheet.Append(CultureInfo.InvariantCulture, $"<autoFilter ref=\"A1:{Column(header.Count - 1)}{data.Count + 1}\"/>");
        sheet.Append("</worksheet>");

        var name = SafeSheetName(sheetName);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                $"<sheets><sheet name=\"{Escape(name)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                (header.Count > 0 ? $"<definedNames><definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"0\" hidden=\"1\">'{Escape(name.Replace("'", "''"))}'!$A$1:${Column(header.Count - 1)}${data.Count + 1}</definedName></definedNames>" : "") +
                "</workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            Add(zip, "xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFE8F0FB\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/></cellXfs>" +
                "<cellStyles count=\"1\"><cellStyle name=\"Standard\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
            Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return ms.ToArray();
    }

    private static void AppendRow(StringBuilder sb, int rowNumber, IReadOnlyList<object?> values, int style)
    {
        sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNumber}\">");
        for (var c = 0; c < values.Count; c++)
        {
            var v = values[c];
            if (v is null || v is string { Length: 0 }) continue;
            var cellRef = Column(c) + rowNumber.ToString(CultureInfo.InvariantCulture);
            var s = style > 0 ? $" s=\"{style}\"" : "";
            switch (v)
            {
                case int or long or short or double or float or decimal:
                    sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{cellRef}\"{s}><v>{Convert.ToString(v, CultureInfo.InvariantCulture)}</v></c>");
                    break;
                default:
                    sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{cellRef}\"{s} t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(Text(v))}</t></is></c>");
                    break;
            }
        }
        sb.Append("</row>");
    }

    private static string Text(object? v) => v switch
    {
        null => "",
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        bool b => b ? "ja" : "nein",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>0 → A, 25 → Z, 26 → AA.</summary>
    public static string Column(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    private static string Escape(string value)
    {
        // Drop characters XML 1.0 does not allow (control characters except tab/CR/LF).
        var clean = new StringBuilder(value.Length);
        foreach (var ch in value)
            if (ch is '\t' or '\n' or '\r' || ch >= ' ') clean.Append(ch);
        return SecurityElement.Escape(clean.ToString()) ?? "";
    }

    private static string SafeSheetName(string name)
    {
        var s = new string(name.Where(ch => ch is not ('[' or ']' or ':' or '*' or '?' or '/' or '\\')).ToArray()).Trim();
        if (s.Length == 0) s = "Tabelle1";
        return s.Length > 31 ? s[..31] : s;
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
