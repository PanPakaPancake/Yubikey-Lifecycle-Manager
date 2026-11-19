using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Ylm.Server.Reporting;
using static Ylm.Server.Pdf.Theme;

namespace Ylm.Server.Pdf;

/// <summary>Übersicht „Wer hat welchen Key“ als PDF (A4 quer), z. B. zum Ausdrucken oder Ablegen.</summary>
public static class AssignmentPdf
{
    private const double Margin = 36;

    public static byte[] Create(string organizationName, string scope, AssignmentOverview o, bool includeTenant)
    {
        YlmFontResolver.Install();
        using var doc = new PdfDocument();
        doc.Info.Title = "Key-Zuweisungen";
        PdfPage page = null!;
        XGraphics g = null!;
        var pageNo = 0;
        var y = 0.0;

        // Spalten: Hat den Key | Seriennummer | Modell / Firmware | Nutzung | Seit | Tenant
        double[] widths = includeTenant ? [0.32, 0.11, 0.18, 0.12, 0.11, 0.16] : [0.40, 0.12, 0.22, 0.13, 0.13];
        string[] header = includeTenant
            ? ["Hat den Key", "Seriennummer", "Modell / Firmware", "Nutzung", "Seit", "Tenant"]
            : ["Hat den Key", "Seriennummer", "Modell / Firmware", "Nutzung", "Seit"];

        void NewPage()
        {
            g?.Dispose();
            page = doc.AddPage();
            page.Width = XUnit.FromMillimeter(297);
            page.Height = XUnit.FromMillimeter(210);
            g = XGraphics.FromPdfPage(page);
            pageNo++;
            var w = page.Width.Point - 2 * Margin;
            g.DrawRectangle(new XSolidBrush(Accent), 0, 0, page.Width.Point, 5);
            Text(g, string.Join(" · ", new[] { organizationName, "Key-Zuweisungen", scope }.Where(x => !string.IsNullOrWhiteSpace(x))), F(7.5), Muted, Margin, page.Height.Point - 24, w - 60, 10);
            DrawBrandFooter(g, Margin, page.Height.Point - 26, w - 60);
            g.DrawString($"Seite {pageNo}", F(7.5), new XSolidBrush(Muted), new XRect(Margin, page.Height.Point - 24, w, 10), XStringFormats.TopRight);
            y = 30;
        }

        void Ensure(double needed, bool repeatHeader = true)
        {
            if (y + needed < page.Height.Point - 40) return;
            NewPage();
            if (repeatHeader) Row(header, header: true);
        }

        void Row(string[] cells, bool header = false, bool sub = false, bool warn = false)
        {
            var w = page.Width.Point - 2 * Margin;
            var h = sub ? 13.0 : 17.0;
            if (header) g.DrawRectangle(new XSolidBrush(AccentSoft), Margin, y - 3, w, h);
            else if (warn) g.DrawRectangle(new XSolidBrush(WarnSoft), Margin, y - 3, w, h);
            var cx = Margin + 5;
            for (var i = 0; i < cells.Length; i++)
            {
                var cw = w * widths[i] - 8;
                var font = sub ? F(7.5) : F(8.5, header || i == 0);
                var text = cells[i];
                while (text.Length > 3 && g.MeasureString(text, font).Width > cw) text = text[..^4] + "…";
                g.DrawString(text, font, new XSolidBrush(sub ? Muted : Ink), new XRect(cx, y, cw, 11), XStringFormats.TopLeft);
                cx += w * widths[i];
            }
            y += h;
            if (!sub) g.DrawLine(new XPen(Line, 0.4), Margin, y - 3, Margin + w, y - 3);
        }

        NewPage();
        var width = page.Width.Point - 2 * Margin;
        DrawLogo(g, Margin + width, 14, 140, 36);
        Text(g, organizationName, F(9, true), Muted, Margin, 22, width - 150, 12);
        Text(g, "Wer hat welchen Key?", F(19, true), Ink, Margin, 35, width, 24);
        Text(g, $"{scope} · Stand {o.GeneratedAt.ToLocalTime():dd.MM.yyyy HH:mm}", F(9), Muted, Margin, 61, width, 12);
        y = 84;

        var s = o.Summary;
        (string Label, string Value)[] kpis =
        [
            ("Ausgegebene Keys", s.Keys.ToString()),
            ("Personen mit Key", s.People.ToString()),
            ("Geteilt / Breakglass", $"{s.Shared} / {s.Breakglass}"),
            ("Mehrere Keys", s.PeopleWithSeveralKeys.ToString()),
            ("Auffälligkeiten", s.WithIssues.ToString()),
        ];
        var tw = (width - (kpis.Length - 1) * 8) / kpis.Length;
        for (var i = 0; i < kpis.Length; i++)
        {
            var x = Margin + i * (tw + 8);
            g.DrawRoundedRectangle(new XPen(Line, 0.8), new XSolidBrush(XColor.FromArgb(0xf7, 0xf9, 0xfc)), x, y, tw, 46, 6, 6);
            Text(g, kpis[i].Label, F(8), Muted, x + 9, y + 7, tw - 18, 11);
            Text(g, kpis[i].Value, F(16, true), i == 4 && s.WithIssues > 0 ? Warn : Ink, x + 9, y + 20, tw - 18, 22);
        }
        y += 62;

        if (o.Keys.Count == 0)
        {
            Text(g, "Keine ausgegebenen Keys für diese Auswahl.", F(10), Muted, Margin, y, width, 14);
        }
        else
        {
            Row(header, header: true);
            foreach (var k in o.Keys)
            {
                var shared = k.Usage != "Personal";
                Ensure(Math.Min(17 + (shared ? 13 * k.Accounts.Count : 0), 120));
                var holder = k.Holder is null ? "– niemand –" : k.HolderUpn is { } upn && upn != k.Holder ? $"{k.Holder} ({upn})" : k.Holder;
                if (shared) holder = (k.Custodian is null ? "Verwahrer fehlt" : "Verwahrer: " + k.Custodian);
                var modelFw = string.Join(" · ", new[] { k.Model, k.FirmwareVersion }.Where(x => !string.IsNullOrEmpty(x)));
                var usage = AssignmentService.UsageLabel(k.Usage) + (shared ? $" ({k.Accounts.Count})" : "");
                string[] cells = [holder, k.SerialNumber?.ToString() ?? "nicht erfasst", modelFw.Length > 0 ? modelFw : "–", usage, k.Since?.ToLocalTime().ToString("dd.MM.yyyy") ?? "–"];
                if (includeTenant) cells = [.. cells, k.TenantName];
                Row(cells, warn: k.Issues.Count > 0);
                if (shared)
                {
                    foreach (var a in k.Accounts)
                    {
                        Ensure(13);
                        string[] sub = ["      · " + (a.UserDisplayName is { } n ? $"{n} ({a.UserPrincipalName})" : a.UserPrincipalName), "", "", "", a.RegisteredAt.ToLocalTime().ToString("dd.MM.yyyy")];
                        if (includeTenant) sub = [.. sub, ""];
                        Row(sub, sub: true);
                    }
                    g.DrawLine(new XPen(Line, 0.4), Margin, y - 3, Margin + width, y - 3);
                }
                foreach (var issue in k.Issues)
                {
                    Ensure(13);
                    string[] sub = ["      Hinweis: " + issue, "", "", "", ""];
                    if (includeTenant) sub = [.. sub, ""];
                    Row(sub, sub: true);
                }
            }
        }

        g.Dispose();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }
}
