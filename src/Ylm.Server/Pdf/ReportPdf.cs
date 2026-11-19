using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Ylm.Core;
using Ylm.Server.Reporting;
using static Ylm.Server.Pdf.Theme;

namespace Ylm.Server.Pdf;

/// <summary>Dashboard as PDF (planning 7, V1 export).</summary>
public static class ReportPdf
{
    public static byte[] Create(string organizationName, string scope, Dashboard d)
    {
        YlmFontResolver.Install();
        using var doc = new PdfDocument();
        doc.Info.Title = "YubiKey-Bericht";
        var page = doc.AddPage();
        page.Width = XUnit.FromMillimeter(210);
        page.Height = XUnit.FromMillimeter(297);
        var g = XGraphics.FromPdfPage(page);
        const double m = 44;
        var w = page.Width.Point - 2 * m;
        var y = 0.0;

        void NewPageIfNeeded(double needed)
        {
            if (y + needed < page.Height.Point - 50) return;
            g.Dispose();
            page = doc.AddPage();
            page.Width = XUnit.FromMillimeter(210);
            page.Height = XUnit.FromMillimeter(297);
            g = XGraphics.FromPdfPage(page);
            DrawBrandFooter(g, m, page.Height.Point - 20, w);
            y = 44;
        }

        g.DrawRectangle(new XSolidBrush(Accent), 0, 0, page.Width.Point, 6);
        DrawLogo(g, m + w, 20, 140, 40);
        DrawBrandFooter(g, m, page.Height.Point - 20, w);
        Text(g, organizationName, F(9.5, true), Muted, m, 26, w - 150, 14);
        Text(g, "YubiKey-Bericht", F(20, true), Ink, m, 40, w, 26);
        Text(g, $"{scope} · Stand {d.GeneratedAt.ToLocalTime():dd.MM.yyyy HH:mm}", F(9.5), Muted, m, 68, w, 14);
        y = 96;

        // KPI tiles
        (string Label, string Value)[] kpis =
        [
            ("Aktiv", d.ByStatus["Active"].ToString()),
            ("Im Lager", d.InStock.ToString()),
            ("Gesperrt", d.ByStatus["Locked"].ToString()),
            ("Abdeckung", d.CoveragePercent is { } c ? $"{c:0.#} %" : "–"),
        ];
        var tw = (w - 3 * 10) / 4;
        for (var i = 0; i < kpis.Length; i++)
        {
            var x = m + i * (tw + 10);
            g.DrawRoundedRectangle(new XPen(Line, 0.8), new XSolidBrush(XColor.FromArgb(0xf7, 0xf9, 0xfc)), x, y, tw, 56, 6, 6);
            Text(g, kpis[i].Label, F(8.5), Muted, x + 10, y + 8, tw - 20, 12);
            Text(g, kpis[i].Value, F(19, true), Ink, x + 10, y + 22, tw - 20, 26);
        }
        y += 72;
        Text(g, $"Personen mit Key: {d.UsersWithKey}{(d.TargetUsers is { } t ? $" von {t} in der Zielgruppe" : "")}. " +
                $"Ausgemustert: {d.ByStatus["Retired"]}. Selbst registrierte Keys ohne Seriennummer: {d.NeedsDetails}.", F(9), Ink, m, y, w, 28);
        y += 30;

        // Issued per month (bar chart)
        Text(g, "Ausgaben pro Monat (über die Plattform registriert)", F(11, true), Ink, m, y, w, 16);
        y += 36;
        var chartH = 90.0;
        var max = Math.Max(1, d.IssuedPerMonth.Count == 0 ? 1 : d.IssuedPerMonth.Max(b => b.Count));
        var bw = w / Math.Max(1, d.IssuedPerMonth.Count);
        g.DrawLine(new XPen(Line, 0.8), m, y + chartH, m + w, y + chartH);
        for (var i = 0; i < d.IssuedPerMonth.Count; i++)
        {
            var b = d.IssuedPerMonth[i];
            var h = chartH * b.Count / max;
            var x = m + i * bw + bw * 0.18;
            if (b.Count > 0)
            {
                g.DrawRoundedRectangle(new XSolidBrush(Accent), x, y + chartH - h, bw * 0.64, h, 3, 3);
                g.DrawString(b.Count.ToString(), F(7.5, true), new XSolidBrush(Ink), new XRect(x, y + chartH - h - 12, bw * 0.64, 10), XStringFormats.Center);
            }
            g.DrawString(b.Month[2..].Replace('-', '/'), F(7), new XSolidBrush(Muted), new XRect(m + i * bw, y + chartH + 3, bw, 10), XStringFormats.Center);
        }
        y += chartH + 26;

        // Firmware table
        NewPageIfNeeded(60);
        Text(g, "Firmware-Verteilung (aktive Keys und Lager)", F(11, true), Ink, m, y, w, 16);
        y += 20;
        Row(g, ["Firmware", "Anzahl", "Hinweise"], m, ref y, w, true);
        foreach (var f in d.Firmware)
        {
            NewPageIfNeeded(18);
            Row(g, [f.Firmware, f.Count.ToString(), f.Advisories.Count == 0 ? "–" : string.Join(", ", f.Advisories.Select(a =>
                FirmwareAdvisories.Known.FirstOrDefault(k => k.Id == a)?.Title is { } title ? $"{a}: {title}" : a))], m, ref y, w, false, f.Vulnerable);
        }
        if (d.Firmware.Count == 0) { Text(g, "Keine Keys mit bekannter Firmware.", F(9), Muted, m, y, w, 14); y += 16; }
        y += 14;

        if (d.Tenants.Count > 1)
        {
            NewPageIfNeeded(60);
            Text(g, "Tenants", F(11, true), Ink, m, y, w, 16);
            y += 20;
            Row(g, ["Tenant", "Aktiv", "Lager", "Personen mit Key", "Letzter Sync"], m, ref y, w, true);
            foreach (dynamic tn in d.Tenants)
            {
                NewPageIfNeeded(18);
                DateTimeOffset? sync = tn.LastSyncAt;
                Row(g, [(string)tn.DisplayName, ((int)tn.Active).ToString(), ((int)tn.InStock).ToString(), ((int)tn.UsersWithKey).ToString(), sync?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "–"], m, ref y, w, false);
            }
        }

        g.Dispose();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static void Row(XGraphics g, string[] cells, double x, ref double y, double w, bool header, bool warn = false)
    {
        double[] widths = cells.Length == 3 ? [0.18, 0.12, 0.70] : [0.36, 0.12, 0.12, 0.2, 0.2];
        if (header) g.DrawRectangle(new XSolidBrush(XColor.FromArgb(0xf1, 0xf3, 0xf7)), x, y - 2, w, 16);
        if (warn) g.DrawRectangle(new XSolidBrush(WarnSoft), x, y - 2, w, 16);
        var cx = x + 4;
        for (var i = 0; i < cells.Length; i++)
        {
            var cw = w * widths[i] - 8;
            var text = cells[i];
            var font = F(8.5, header);
            while (text.Length > 3 && g.MeasureString(text, font).Width > cw) text = text[..^4] + "…";
            g.DrawString(text, font, new XSolidBrush(warn && i == 2 ? Warn : Ink), new XRect(cx, y, cw, 12), XStringFormats.TopLeft);
            cx += w * widths[i];
        }
        y += 16;
        g.DrawLine(new XPen(Line, 0.5), x, y - 2, x + w, y - 2);
    }
}
