using PdfSharp.Drawing;
using PdfSharp.Pdf;
using static Ylm.Server.Pdf.Theme;

namespace Ylm.Server.Pdf;

public sealed record PinLetterData(
    string OrganizationName,
    string TenantName,
    string UserDisplayName,
    string UserPrincipalName,
    string Pin,
    string KeyModel,
    int? SerialNumber,
    bool PinChangeForced,
    DateTime CreatedAt,
    /// <summary>Geteilter Key: Konten auf dem Key. Der Brief geht dann an den Verwahrer (UserDisplayName).</summary>
    IReadOnlyList<string>? SharedAccounts = null,
    /// <summary>Link zur öffentlichen Passkey-Infoseite; ohne Adresse der Plattform entfällt der Hinweis.</summary>
    string? InfoUrl = null);

/// <summary>PIN letter for the end user with a simple illustrated guide (planning 6.2).</summary>
public static class PinLetterPdf
{
    /// <summary>Öffentliche Adresse der Plattform: Konfiguration (Ylm__PublicBaseUrl), sonst Setup, sonst die aktuelle Anfrage.</summary>
    public static async Task<string?> BaseUrlAsync(Data.YlmDbContext db, Infrastructure.YlmOptions options, HttpRequest? request = null, CancellationToken ct = default)
    {
        var url = options.PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = (await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AsNoTracking(db.Installation), ct))?.PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(url) && request is not null) url = $"{request.Scheme}://{request.Host}";
        return string.IsNullOrWhiteSpace(url) ? null : url.TrimEnd('/');
    }

    /// <summary>Link zur Infoseite, mit Tenant für dessen Erscheinungsbild.</summary>
    public static string? InfoUrl(string? baseUrl, Guid? tenantId) =>
        baseUrl is null ? null : $"{baseUrl}/passkey" + (tenantId is { } t ? $"?t={t}" : "");

    public static byte[] Create(PinLetterData d)
    {
        YlmFontResolver.Install();
        using var doc = new PdfDocument();
        doc.Info.Title = d.PinChangeForced ? "Initial-PIN für Ihren YubiKey" : "PIN für Ihren YubiKey";
        doc.Info.Creator = Brand.CompanyName ?? "Yubikey Lifecycle Manager";
        new Renderer(doc, d).Run();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    /// <summary>Werte für die Platzhalter im Brieftext.</summary>
    public static Dictionary<string, string?> Values(PinLetterData d) => new()
    {
        ["Name"] = d.UserDisplayName,
        ["Vorname"] = d.UserDisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? d.UserDisplayName,
        ["Anmeldename"] = d.UserPrincipalName,
        ["Firma"] = Brand.CompanyName ?? d.OrganizationName,
        ["Tenant"] = d.TenantName,
        ["Modell"] = d.KeyModel,
        ["Seriennummer"] = d.SerialNumber?.ToString() ?? "–",
        ["Datum"] = d.CreatedAt.ToString("dd.MM.yyyy"),
        ["Kontakt"] = Brand.Contact ?? "",
        ["KontaktMitDoppelpunkt"] = Brand.Contact is { Length: > 0 } c ? ": " + c : "",
    };

    private const double M = 48, Top = 34, Bottom = 790, FooterY = 800, Gap = 12, ColGap = 14;

    /// <summary>
    /// Zeichnet die Bausteine des Briefaufbaus untereinander; zwei halbe Bausteine stehen nebeneinander.
    /// Passt ein Baustein nicht mehr auf die Seite, beginnt eine neue Seite. Der Baustein „Neue Seite“ erzwingt einen Seitenwechsel;
    /// Seiten ohne sichtbaren Inhalt (etwa weil alle Bausteine in dieser Variante ausgeblendet sind) entfallen.
    /// </summary>
    private sealed class Renderer(PdfDocument doc, PinLetterData d)
    {
        private readonly Dictionary<string, string?> _values = Values(d);
        private readonly List<PdfPage> _pages = [];
        private PdfPage _page = null!;
        private XGraphics _g = null!;
        /// <summary>true, solange nur gemessen wird (Messkontext, nichts wird gezeichnet).</summary>
        private bool _measuring;

        private string R(string text) => LetterTemplate.Render(text, _values);

        public void Run()
        {
            var layout = Brand.EffectiveLayout();
            var blocks = layout.Blocks.Where(b => b.VisibleFor(d.PinChangeForced) && LetterBlocks.Find(b.Type) is not null)
                .Where(b => b.Type != "info" || d.InfoUrl is not null).ToList();
            if (blocks.All(b => b.Type != "pin")) blocks.Add(new LetterBlock { Id = "pin", Type = "pin" }); // die PIN muss immer drauf

            // Zeilen bilden: zwei aufeinanderfolgende halbe Bausteine teilen sich eine Zeile.
            var rows = new List<LetterBlock[]>();
            for (var i = 0; i < blocks.Count; i++)
            {
                if (blocks[i].Width == "half" && i + 1 < blocks.Count && blocks[i + 1].Width == "half") { rows.Add([blocks[i], blocks[i + 1]]); i++; }
                else rows.Add([blocks[i]]);
            }

            NewPage();
            var w = _page.Width.Point - 2 * M;
            var half = (w - ColGap) / 2;
            using var measure = XGraphics.CreateMeasureContext(new XSize(_page.Width.Point, _page.Height.Point), XGraphicsUnit.Point, XPageDirection.Downwards);
            var y = Top;
            var pageBreak = false;
            foreach (var row in rows)
            {
                // Neue Seite erst, wenn danach wirklich etwas kommt: so entstehen keine leeren Seiten.
                if (row[0].Type == "pageBreak") { pageBreak = true; continue; }
                double Width(LetterBlock b) => row.Length == 2 || b.Width == "half" ? half : w;
                var real = _g;
                _g = measure; _measuring = true;
                var h = row.Max(b => Draw(b, M, 0, Width(b)));
                _g = real; _measuring = false;
                if (h <= 0) continue;
                if ((pageBreak || y + h > Bottom) && y > Top + 1) { NewPage(); y = Top; }
                pageBreak = false;
                for (var i = 0; i < row.Length; i++) Draw(row[i], M + i * (half + ColGap), y, Width(row[i]));
                y += h + (row.All(b => b.Type is "spacer" or "divider") ? 4 : Gap);
            }
            _g.Dispose();

            for (var i = 0; i < _pages.Count; i++)
            {
                using var g = XGraphics.FromPdfPage(_pages[i], XGraphicsPdfPageOptions.Append);
                var parts = new[] { _pages.Count > 1 ? $"Seite {i + 1} von {_pages.Count}" : null, d.OrganizationName, d.TenantName,
                    $"{Brand.Footer ?? "Erstellt mit dem Yubikey Lifecycle Manager."} Die PIN wurde nicht gespeichert." };
                g.DrawLine(new XPen(Line, 0.7), M, FooterY, M + w, FooterY);
                Text(g, string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p))), F(7.5), Muted, M, FooterY + 6, w, 12);
            }
        }

        private void NewPage()
        {
            _g?.Dispose();
            _page = doc.AddPage();
            _page.Width = XUnit.FromMillimeter(210);
            _page.Height = XUnit.FromMillimeter(297);
            _pages.Add(_page);
            _g = XGraphics.FromPdfPage(_page);
        }

        /// <returns>Höhe des Bausteins.</returns>
        private double Draw(LetterBlock b, double x, double y, double w) => b.Type switch
        {
            "header" => Header(x, y, w),
            "heading" => Heading(b, x, y, w),
            "recipient" => Recipient(b, x, y, w),
            "text" => LetterTemplate.IsOmitted(b.P("text")) ? 0 : Block(_g, R(b.P("text")), b.P("size") == "s" ? F(8.6) : F(10), Ink, x, y, w, 1.35),
            "pin" => Pin(b, x, y, w),
            "steps" => Steps(b, x, y, w),
            "pinChange" => PinChange(b, x, y, w),
            "hints" => Hints(b, x, y, w),
            "callout" => Callout(b, x, y, w),
            "info" => Info(b, x, y, w),
            "divider" => Divider(x, y, w),
            "spacer" => b.P("size") switch { "s" => 4, "l" => 34, _ => 16 },
            _ => 0,
        };

        private double Header(double x, double y, double w)
        {
            var hasLogo = !_measuring && DrawLogo(_g, x + w, y, Math.Min(140, w * 0.4), 32);
            if (_measuring) hasLogo = Brand.Logo is { Length: > 0 };
            var name = (Brand.CompanyName ?? d.OrganizationName).ToUpperInvariant();
            if (!hasLogo && string.IsNullOrWhiteSpace(name)) return 0;
            var nameW = hasLogo ? w - Math.Min(150, w * 0.42) : w;
            Block(_g, Shorten(name, 90), Fit(_g, name, 8.5, true, nameW, 6.5), Accent, x, y + (hasLogo ? 11 : 2), nameW);
            return hasLogo ? 32 : 14;
        }

        private double Heading(LetterBlock b, double x, double y, double w)
        {
            var (size, factor) = b.P("size") switch { "s" => (11.0, 1.2), "m" => (14.0, 1.2), _ => (23.0, 1.15) };
            return Block(_g, R(b.P("text")), F(size, true), Ink, x, y, w, factor);
        }

        private double Recipient(LetterBlock b, double x, double y, double w)
        {
            var text = d.SharedAccounts is { Count: > 0 }
                ? $"Für den Verwahrer {d.UserDisplayName} · {d.UserPrincipalName} für {d.SharedAccounts.Count} Konto/Konten: {Shorten(string.Join(", ", d.SharedAccounts), 110)}"
                : R(b.P("text"));
            var h = Block(_g, text, F(11), Ink, x, y, w);
            if (b.P("keyLine") != "false")
                h += 2 + Block(_g, $"{d.KeyModel}{(d.SerialNumber is { } s ? $" · Seriennummer {s}" : "")} · erstellt am {d.CreatedAt:dd.MM.yyyy}", F(8.8), Muted, x, y + h + 2, w);
            return h;
        }

        private double Divider(double x, double y, double w)
        {
            _g.DrawLine(new XPen(Accent, 1.4), x, y + 2, x + Math.Min(46, w * 0.2), y + 2);
            _g.DrawLine(new XPen(Line, 0.7), x + Math.Min(50, w * 0.2 + 4), y + 2, x + w, y + 2);
            return 4;
        }

        /// <summary>
        /// PIN-Kasten. Mit erzwungenem PIN-Wechsel: gestrichelter oranger Rahmen, Bezeichnung „Initial-PIN“ und Kennzeichnung,
        /// damit sofort klar ist, dass diese PIN nur für die erste Anmeldung gilt.
        /// </summary>
        private double Pin(LetterBlock b, double x, double y, double w)
        {
            var forced = d.PinChangeForced;
            var color = forced ? Warn : Accent;
            var label = R(forced ? b.P("forcedLabel") : b.P("ownLabel")).ToUpperInvariant();
            var badge = forced ? R(b.P("forcedBadge")).Trim() : "";
            var hint = R(forced ? b.P("forcedText") : b.P("ownText"));
            var digitsW = Math.Min(180, w - 36);
            var stacked = w < 380; // schmal: Hinweis unter die Ziffern statt daneben
            var leftW = stacked ? w - 36 : digitsW;
            var labelFont = Fit(_g, label, 7.5, true, leftW, 6);
            var labelH = Measure(_g, label, labelFont, leftW);
            var badgeFont = Fit(_g, badge, 7, true, leftW - 12, 5.5);
            var badgeW = badge.Length > 0 ? Math.Min(leftW, _g.MeasureString(badge, badgeFont).Width + 12) : 0;
            // Kennzeichnung neben die Bezeichnung, wenn Platz ist, sonst darunter.
            var labelW = _g.MeasureString(label, labelFont).Width;
            var inline = badge.Length > 0 && labelH < 12 && labelW + 8 + badgeW <= leftW;
            var badgeX = inline ? x + 18 + labelW + 8 : x + 18;
            var badgeY = inline ? y + 12 : y + 16 + labelH;
            var badgeH = badge.Length > 0 && !inline ? 17.0 : inline ? 4 : 0;
            var digitsY = y + 18 + labelH + badgeH;
            var hintW = stacked ? w - 36 : w - digitsW - 54;
            var hintH = Measure(_g, hint, F(9.3), hintW, 1.3);
            var leftH = 18 + labelH + badgeH + 36 + 14;
            var boxH = stacked ? leftH + hintH + 8 : Math.Max(leftH, hintH + 30);
            if (_measuring) return boxH;
            var pen = new XPen(color, forced ? 1.3 : 1.1);
            if (forced) pen.DashStyle = XDashStyle.Dash;
            _g.DrawRoundedRectangle(pen, XBrushes.White, x, y, w, boxH, 12, 12);
            Block(_g, label, labelFont, color, x + 18, y + 14, leftW);
            if (badge.Length > 0)
            {
                _g.DrawRoundedRectangle(new XPen(Warn, 0.8), new XSolidBrush(WarnSoft), badgeX, badgeY, badgeW, 13, 6.5, 6.5);
                _g.DrawString(badge, badgeFont, new XSolidBrush(Warn), new XRect(badgeX, badgeY, badgeW, 13), XStringFormats.Center);
            }
            DrawPin(_g, d.Pin, x + 18, digitsY, digitsW, color);
            if (stacked) Block(_g, hint, F(9.3), Ink, x + 18, y + leftH - 4, hintW, 1.3);
            else
            {
                _g.DrawLine(new XPen(Line, 0.7), x + digitsW + 36, y + 14, x + digitsW + 36, y + boxH - 14);
                Block(_g, hint, F(9.3), Ink, x + digitsW + 50, y + (boxH - hintH) / 2, hintW, 1.3);
            }
            return boxH;
        }

        private double Steps(LetterBlock b, double x, double y, double w)
        {
            var titleH = Block(_g, R(b.P("title")), F(13, true), Ink, x, y, w) + 10;
            var top = y + titleH;
            var colW = (w - 3 * 10) / 4;
            // Höhe nach dem längsten Text, damit nichts aus dem Rahmen läuft.
            var textH = 0.0;
            for (var i = 0; i < 4; i++)
            {
                var th = Measure(_g, R(b.P($"step{i + 1}Title")), F(9.6, true), colW - 40);
                textH = Math.Max(textH, Math.Max(th, 14) + 6 + Measure(_g, R(b.P($"step{i + 1}Text")), F(8.4), colW - 20, 1.3));
            }
            var cardH = Math.Max(150, 100 + textH + 10);
            if (_measuring) return titleH + cardH;
            var linePen = new XPen(Line, 0.7);
            var accentPen = new XPen(Accent, 1.1);
            for (var i = 0; i < 4; i++)
            {
                var cx = x + i * (colW + 10);
                _g.DrawRoundedRectangle(linePen, XBrushes.White, cx, top, colW, cardH, 10, 10);
                var art = new XRect(cx + 8, top + 6, colW - 16, 76);
                switch (i)
                {
                    case 0: DrawPlugIn(_g, art); break;
                    case 1: DrawSignInOptions(_g, art); break;
                    case 2: DrawPinEntry(_g, art); break;
                    default: DrawTouch(_g, art); break;
                }
                _g.DrawLine(linePen, cx + 10, top + 88, cx + colW - 10, top + 88);
                _g.DrawEllipse(accentPen, XBrushes.White, cx + 10, top + 96, 16, 16);
                _g.DrawString((i + 1).ToString(), F(8, true), new XSolidBrush(Accent), new XRect(cx + 10, top + 96, 16, 16), XStringFormats.Center);
                var th = Block(_g, R(b.P($"step{i + 1}Title")), F(9.6, true), Ink, cx + 31, top + 98, colW - 40);
                Block(_g, R(b.P($"step{i + 1}Text")), F(8.4), Muted, cx + 10, top + 98 + Math.Max(th, 14) + 6, colW - 20, 1.3);
            }
            return titleH + cardH;
        }

        /// <summary>Kasten mit Akzentstreifen; jede Zeile ein Punkt, Text vor einem Doppelpunkt fett.</summary>
        private double PinChange(LetterBlock b, double x, double y, double w)
        {
            var lines = R(b.P("text")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var inner = w - 32;
            var h = 10 + Measure(_g, R(b.P("title")), F(10, true), inner) + 6;
            var heights = new List<double>();
            foreach (var l in lines) { var lh = LabeledHeight(l, inner); heights.Add(lh); h += lh + 4; }
            h += 6;
            if (_measuring) return h;
            _g.DrawRoundedRectangle(new XPen(Accent, 0.9), XBrushes.White, x, y, w, h, 10, 10);
            _g.DrawRectangle(new XSolidBrush(Accent), x, y + 10, 3, h - 20);
            var cy = y + 10 + Block(_g, R(b.P("title")), F(10, true), Accent, x + 16, y + 10, inner) + 6;
            for (var i = 0; i < lines.Length; i++) { DrawLabeled(lines[i], x + 16, cy, inner); cy += heights[i] + 4; }
            return h;
        }

        /// <summary>Teilt „Windows: …“ in fetten Anfang und normalen Text; ohne kurzen Anfang vor dem Doppelpunkt alles normal.</summary>
        private static (string? Label, string Text) SplitLabel(string line)
        {
            var i = line.IndexOf(": ", StringComparison.Ordinal);
            return i is > 0 and <= 32 && !line[..i].Contains("//") ? (line[..(i + 1)], line[(i + 2)..]) : (null, line);
        }

        private double LabeledHeight(string line, double w)
        {
            var (label, text) = SplitLabel(line);
            if (label is null) return Measure(_g, text, F(9), w, 1.3);
            var lw = _g.MeasureString(label + " ", F(9, true)).Width;
            return lw > w * 0.35 ? Measure(_g, line, F(9), w, 1.3) : Measure(_g, text, F(9), w - lw, 1.3);
        }

        private void DrawLabeled(string line, double x, double y, double w)
        {
            var (label, text) = SplitLabel(line);
            var lw = label is null ? 0 : _g.MeasureString(label + " ", F(9, true)).Width;
            if (label is null || lw > w * 0.35) { Block(_g, line, F(9), Ink, x, y, w, 1.3); return; }
            Block(_g, label, F(9, true), Ink, x, y, lw);
            Block(_g, text, F(9), Ink, x + lw, y, w - lw, 1.3);
        }

        private double Hints(LetterBlock b, double x, double y, double w)
        {
            var h = Block(_g, R(b.P("title")), F(11, true), Ink, x, y, w) + 6;
            foreach (var item in R(b.P("items")).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!_measuring)
                {
                    var pen = new XPen(Accent, 1.3) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
                    _g.DrawLines(pen, [new XPoint(x + 1, y + h + 6), new XPoint(x + 4, y + h + 9), new XPoint(x + 9, y + h + 3)]);
                }
                h += Block(_g, item, F(9), Ink, x + 16, y + h, w - 16, 1.3) + 5;
            }
            return h - 5;
        }

        private double Callout(LetterBlock b, double x, double y, double w)
        {
            var color = b.P("tone") switch { "warn" => Warn, "neutral" => Muted, _ => Accent };
            var inner = w - 32;
            var title = R(b.P("title")).Trim();
            var titleH = title.Length > 0 ? Measure(_g, title, F(10, true), inner) + 4 : 0;
            var h = 10 + titleH + Measure(_g, R(b.P("text")), F(9), inner, 1.3) + 10;
            if (_measuring) return h;
            _g.DrawRoundedRectangle(new XPen(color, 0.9), XBrushes.White, x, y, w, h, 10, 10);
            _g.DrawRectangle(new XSolidBrush(color), x, y + 10, 3, h - 20);
            if (titleH > 0) Block(_g, title, F(10, true), color, x + 16, y + 10, inner);
            Block(_g, R(b.P("text")), F(9), Ink, x + 16, y + 10 + titleH, inner, 1.3);
            return h;
        }

        private double Info(LetterBlock b, double x, double y, double w)
        {
            var inner = w - 32;
            var shown = d.InfoUrl!.Split('?')[0].Replace("https://", "").Replace("http://", "") + "  →";
            var titleH = Measure(_g, R(b.P("title")), F(9.5, true), inner);
            var urlFont = Fit(_g, shown, 10, false, inner, 6.5);
            var h = 9 + titleH + 3 + urlFont.Size * 1.3 + 9;
            if (_measuring) return h;
            _g.DrawRoundedRectangle(new XPen(Accent, 1.1), XBrushes.White, x, y, w, h, 10, 10);
            Block(_g, R(b.P("title")), F(9.5, true), Accent, x + 16, y + 9, inner);
            Block(_g, shown, urlFont, Ink, x + 16, y + 9 + titleH + 3, inner);
            // Klickbarer Bereich über dem ganzen Kasten.
            var rect = _g.Transformer.WorldToDefaultPage(new XRect(x, y, w, h));
            _page.AddWebLink(new PdfRectangle(rect), d.InfoUrl);
            return h;
        }
    }

    /// <summary>Text mit Zeilenumbruch zeichnen; gibt die Höhe zurück.</summary>
    private static double Block(XGraphics g, string text, XFont font, XColor color, double x, double y, double w, double lineFactor = 1.2)
    {
        var lines = Wrap(g, text, font, w);
        var lh = font.Size * lineFactor;
        var brush = new XSolidBrush(color);
        for (var i = 0; i < lines.Count; i++)
            g.DrawString(lines[i], font, brush, new XRect(x, y + i * lh, w, lh), XStringFormats.TopLeft);
        return lines.Count * lh;
    }

    private static double Measure(XGraphics g, string text, XFont font, double w, double lineFactor = 1.2) =>
        Wrap(g, text, font, w).Count * font.Size * lineFactor;

    /// <summary>Schrift so weit verkleinern, dass der Text in eine Zeile der Breite passt (höchstens bis minSize).</summary>
    private static XFont Fit(XGraphics g, string text, double size, bool bold, double maxW, double minSize)
    {
        var font = F(size, bold);
        while (size > minSize && g.MeasureString(text, font).Width > maxW)
        {
            size -= 0.25;
            font = F(size, bold);
        }
        return font;
    }

    /// <summary>Bricht nach Wörtern um; ein Wort, das allein zu breit ist, wird zeichenweise umbrochen, damit nichts aus dem Rahmen läuft.</summary>
    private static List<string> Wrap(XGraphics g, string text, XFont font, double w)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var raw in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var word = raw;
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (g.MeasureString(candidate, font).Width <= w) { line = candidate; continue; }
                if (line.Length > 0) { lines.Add(line); line = ""; }
                while (word.Length > 1 && g.MeasureString(word, font).Width > w)
                {
                    var n = word.Length - 1;
                    while (n > 1 && g.MeasureString(word[..n], font).Width > w) n--;
                    lines.Add(word[..n]);
                    word = word[n..];
                }
                line = word;
            }
            lines.Add(line);
        }
        return lines;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void DrawPin(XGraphics g, string pin, double x, double y, double maxW, XColor color)
    {
        var cell = Math.Min(30, maxW / Math.Max(pin.Length, 1));
        var font = F(Math.Min(24, cell * 0.72), true);
        for (var i = 0; i < pin.Length; i++)
        {
            var cx = x + i * cell;
            g.DrawRoundedRectangle(new XPen(Mix(color, 0.45), 0.9), XBrushes.White, cx, y, cell - 5, 36, 6, 6);
            g.DrawString(pin[i].ToString(), font, new XSolidBrush(Ink), new XRect(cx, y, cell - 5, 36), XStringFormats.Center);
        }
    }

    private static void DrawKey(XGraphics g, double x, double y, double len, double h, bool vertical, bool glow)
    {
        // Simple YubiKey: dark body, gold touch disc, metal USB connector.
        if (vertical)
        {
            g.DrawRectangle(new XPen(KeyBody, 0.8), new XSolidBrush(XColor.FromArgb(0xe4, 0xe7, 0xec)), x + h * 0.22, y + len * 0.72, h * 0.56, len * 0.28);
            g.DrawRoundedRectangle(new XPen(KeyBody, 1), new XSolidBrush(KeyLight), x, y, h, len * 0.74, h * 0.4, h * 0.4);
            var cx = x + h / 2; var cy = y + len * 0.3;
            if (glow) for (var r = 3; r >= 1; r--) g.DrawEllipse(new XPen(XColor.FromArgb(70 * r, Gold), 1), cx - h * 0.25 - r * 4, cy - h * 0.25 - r * 4, h * 0.5 + r * 8, h * 0.5 + r * 8);
            g.DrawEllipse(new XSolidBrush(Gold), cx - h * 0.25, cy - h * 0.25, h * 0.5, h * 0.5);
            g.DrawEllipse(new XPen(XColor.FromArgb(0x55, 0x5b, 0x66), 1), cx - h * 0.12, y + 4, h * 0.24, h * 0.24);
        }
        else
        {
            g.DrawRectangle(new XPen(KeyBody, 0.8), new XSolidBrush(XColor.FromArgb(0xe4, 0xe7, 0xec)), x + len * 0.72, y + h * 0.22, len * 0.28, h * 0.56);
            g.DrawRoundedRectangle(new XPen(KeyBody, 1), new XSolidBrush(KeyLight), x, y, len * 0.74, h, h * 0.4, h * 0.4);
            var cx = x + len * 0.3; var cy = y + h / 2;
            g.DrawEllipse(new XSolidBrush(Gold), cx - h * 0.25, cy - h * 0.25, h * 0.5, h * 0.5);
        }
    }

    private static void DrawPlugIn(XGraphics g, XRect a)
    {
        // Laptop side with a USB port, key moving in.
        var baseY = a.Y + a.Height * 0.62;
        g.DrawRoundedRectangle(new XPen(Muted, 0.8), new XSolidBrush(XColor.FromArgb(0xf1, 0xf3, 0xf6)), a.X + a.Width * 0.62, baseY - 16, a.Width * 0.34, 30, 4, 4);
        g.DrawRectangle(new XPen(Muted, 0.8), XBrushes.White, a.X + a.Width * 0.62, baseY - 6, 6, 10);
        DrawKey(g, a.X + a.Width * 0.08, baseY - 9, a.Width * 0.5, 18, false, false);
        var pen = new XPen(Accent, 1.6) { LineCap = XLineCap.Round };
        var ax = a.X + a.Width * 0.18; var ay = a.Y + a.Height * 0.3;
        g.DrawLine(pen, ax, ay, ax + a.Width * 0.3, ay);
        g.DrawLine(pen, ax + a.Width * 0.3, ay, ax + a.Width * 0.3 - 6, ay - 5);
        g.DrawLine(pen, ax + a.Width * 0.3, ay, ax + a.Width * 0.3 - 6, ay + 5);
    }

    private static void DrawSignInOptions(XGraphics g, XRect a)
    {
        var win = new XRect(a.X + 8, a.Y + 8, a.Width - 16, a.Height - 16);
        g.DrawRoundedRectangle(new XPen(Line, 0.8), XBrushes.White, win.X, win.Y, win.Width, win.Height, 4, 4);
        g.DrawRectangle(new XSolidBrush(XColor.FromArgb(0xf2, 0x50, 0x22)), win.X + 8, win.Y + 8, 5, 5);
        g.DrawRectangle(new XSolidBrush(XColor.FromArgb(0x7f, 0xba, 0x00)), win.X + 14, win.Y + 8, 5, 5);
        g.DrawRectangle(new XSolidBrush(XColor.FromArgb(0x00, 0xa4, 0xef)), win.X + 8, win.Y + 14, 5, 5);
        g.DrawRectangle(new XSolidBrush(XColor.FromArgb(0xff, 0xb9, 0x00)), win.X + 14, win.Y + 14, 5, 5);
        g.DrawString("Anmelden", Fit(g, "Anmelden", 7, true, win.Width - 32, 4), new XSolidBrush(Ink), win.X + 26, win.Y + 16);
        g.DrawRectangle(new XSolidBrush(Line), win.X + 8, win.Y + 26, win.Width - 16, 7);
        var btn = new XRect(win.X + 8, win.Y + 40, win.Width - 16, 18);
        g.DrawRoundedRectangle(new XPen(Accent, 1), new XSolidBrush(AccentSoft), btn.X, btn.Y, btn.Width, btn.Height, 3, 3);
        DrawKey(g, btn.X + 4, btn.Y + 5, 18, 8, false, false);
        // Schrift an die Breite der Schaltfläche anpassen, damit der Text nicht aus dem Rahmen läuft.
        var label = "Sicherheitsschlüssel";
        var font = Fit(g, label, 5.4, true, btn.Width - 30, 3.2);
        if (g.MeasureString(label, font).Width > btn.Width - 30) { label = "Schlüssel"; font = Fit(g, label, 5.4, true, btn.Width - 30, 3.2); }
        g.DrawString(label, font, new XSolidBrush(Accent), new XRect(btn.X + 26, btn.Y, btn.Width - 28, btn.Height), XStringFormats.CenterLeft);
    }

    private static void DrawPinEntry(XGraphics g, XRect a)
    {
        var box = new XRect(a.X + 10, a.Y + a.Height * 0.32, a.Width - 20, 22);
        g.DrawString("Sicherheitsschlüssel-PIN", Fit(g, "Sicherheitsschlüssel-PIN", 7, true, a.Width - 20, 4), new XSolidBrush(Ink), a.X + 10, a.Y + a.Height * 0.25);
        g.DrawRoundedRectangle(new XPen(Accent, 1), XBrushes.White, box.X, box.Y, box.Width, box.Height, 3, 3);
        var dots = Math.Min(6, (int)((box.Width - 12) / 9));
        for (var i = 0; i < dots; i++) g.DrawEllipse(new XSolidBrush(Muted), box.X + 8 + i * 9, box.Y + 8, 5, 5);
        g.DrawRoundedRectangle(new XPen(Accent, 1), new XSolidBrush(AccentSoft), a.X + a.Width - 48, box.Y + 30, 38, 15, 3, 3);
        g.DrawString("OK", F(7, true), new XSolidBrush(Accent), new XRect(a.X + a.Width - 48, box.Y + 30, 38, 15), XStringFormats.Center);
    }

    private static void DrawTouch(XGraphics g, XRect a)
    {
        var keyH = 22.0;
        DrawKey(g, a.X + a.Width / 2 - keyH / 2, a.Y + 10, a.Height - 20, keyH, true, true);
        // Fingertip
        var fx = a.X + a.Width / 2 + 10; var fy = a.Y + 18;
        g.DrawEllipse(new XPen(Muted, 1.2), new XSolidBrush(XColor.FromArgb(0xf6, 0xd7, 0xc3)), fx, fy, 22, 30);
        g.DrawLine(new XPen(Muted, 1.2), fx + 4, fy + 26, fx + 4, fy + 52);
        g.DrawLine(new XPen(Muted, 1.2), fx + 18, fy + 26, fx + 18, fy + 52);
    }
}
