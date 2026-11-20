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
        doc.Info.Title = "PIN für Ihren YubiKey";
        doc.Info.Creator = Brand.CompanyName ?? "Yubikey Lifecycle Manager";
        var page = doc.AddPage();
        page.Width = XUnit.FromMillimeter(210);
        page.Height = XUnit.FromMillimeter(297);
        using (var g = XGraphics.FromPdfPage(page))
        {
            var infoY = Draw(g, d, page.Width.Point);
            if (d.InfoUrl is { } url && infoY is { } iy)
            {
                // Klickbarer Bereich über dem Hinweis auf die Infoseite.
                var rect = g.Transformer.WorldToDefaultPage(new XRect(InfoBoxX, iy, page.Width.Point - 2 * InfoBoxX, InfoBoxH));
                page.AddWebLink(new PdfRectangle(rect), url);
            }
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private const double InfoBoxX = 48, InfoBoxH = 46;

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

    /// <returns>Oberkante des Hinweises auf die Infoseite, falls gezeichnet.</returns>
    private static double? Draw(XGraphics g, PinLetterData d, double pageWidth)
    {
        const double m = 48;
        var w = pageWidth - 2 * m;
        var values = Values(d);
        string T(string key) => LetterTemplate.Render(Brand.Letter(key), values);
        var accentPen = new XPen(Accent, 1.1);
        var linePen = new XPen(Line, 0.7);

        // Kopf: hell und tonersparend, nur eine feine Akzentlinie statt eines Farbbalkens.
        var hasLogo = DrawLogo(g, m + w, 34, 140, 38);
        Text(g, (Brand.CompanyName ?? d.OrganizationName).ToUpperInvariant(), F(8.5, true), Accent, m, 40, hasLogo ? w - 150 : w, 12);
        var y = 56.0;
        y += Block(g, T("title"), F(23, true), Ink, m, y, hasLogo ? w - 150 : w, 1.15) + 6;
        var recipient = d.SharedAccounts is { Count: > 0 }
            ? $"Für den Verwahrer {d.UserDisplayName} · {d.UserPrincipalName} für {d.SharedAccounts.Count} Konto/Konten: {Shorten(string.Join(", ", d.SharedAccounts), 110)}"
            : T("recipient");
        y += Block(g, recipient, F(11), Ink, m, y, w) + 2;
        y += Block(g, $"{d.KeyModel}{(d.SerialNumber is { } s ? $" · Seriennummer {s}" : "")} · erstellt am {d.CreatedAt:dd.MM.yyyy}", F(8.8), Muted, m, y, w) + 10;
        g.DrawLine(new XPen(Accent, 1.4), m, y, m + 46, y);
        g.DrawLine(linePen, m + 50, y, m + w, y);
        y += 16;

        var intro = T("intro");
        if (!LetterTemplate.IsOmitted(intro)) y += Block(g, intro, F(10), Ink, m, y, w, 1.35) + 14;

        // PIN: weiße Fläche mit Akzentrahmen
        var pinHint = d.PinChangeForced ? T("pinForced") : T("pinOwn");
        var hintH = Measure(g, pinHint, F(9.3), w - 210, 1.3);
        var boxH = Math.Max(78, 30 + hintH + 16);
        g.DrawRoundedRectangle(accentPen, XBrushes.White, m, y, w, boxH, 12, 12);
        Text(g, "IHRE PERSÖNLICHE PIN", F(7.5, true), Accent, m + 18, y + 14, 180, 10);
        DrawPin(g, d.Pin, m + 18, y + 30, 180);
        g.DrawLine(linePen, m + 198, y + 14, m + 198, y + boxH - 14);
        Block(g, pinHint, F(9.3), Ink, m + 212, y + (boxH - hintH) / 2, w - 226, 1.3);
        y += boxH + 22;

        // Anleitung in vier Karten
        y += Block(g, T("guideTitle"), F(13, true), Ink, m, y, w) + 10;
        var colW = (w - 3 * 10) / 4;
        var cardH = 196.0;
        for (var i = 0; i < 4; i++)
        {
            var x = m + i * (colW + 10);
            g.DrawRoundedRectangle(linePen, XBrushes.White, x, y, colW, cardH, 10, 10);
            var art = new XRect(x + 8, y + 8, colW - 16, 84);
            switch (i)
            {
                case 0: DrawPlugIn(g, art); break;
                case 1: DrawSignInOptions(g, art); break;
                case 2: DrawPinEntry(g, art); break;
                default: DrawTouch(g, art); break;
            }
            g.DrawLine(linePen, x + 10, y + 98, x + colW - 10, y + 98);
            g.DrawEllipse(accentPen, XBrushes.White, x + 10, y + 106, 16, 16);
            g.DrawString((i + 1).ToString(), F(8, true), new XSolidBrush(Accent), new XRect(x + 10, y + 106, 16, 16), XStringFormats.Center);
            Block(g, T($"step{i + 1}Title"), F(9.6, true), Ink, x + 31, y + 108, colW - 40);
            Block(g, T($"step{i + 1}Text"), F(8.4), Muted, x + 10, y + 130, colW - 20, 1.3);
        }
        y += cardH + 18;

        if (!d.PinChangeForced)
        {
            var text = T("changeText");
            var h = Measure(g, text, F(9), w - 40, 1.3) + 34;
            g.DrawRoundedRectangle(new XPen(Warn, 0.9), XBrushes.White, m, y, w, h, 10, 10);
            g.DrawRectangle(new XSolidBrush(Warn), m, y + 10, 3, h - 20);
            Block(g, T("changeTitle"), F(10, true), Warn, m + 16, y + 10, w - 32);
            Block(g, text, F(9), Ink, m + 16, y + 26, w - 32, 1.3);
            y += h + 16;
        }

        // Hinweise mit Haken in Akzentfarbe
        y += Block(g, T("hintsTitle"), F(11, true), Ink, m, y, w) + 6;
        foreach (var h in T("hints").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pen = new XPen(Accent, 1.3) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
            g.DrawLines(pen, [new XPoint(m + 1, y + 6), new XPoint(m + 4, y + 9), new XPoint(m + 9, y + 3)]);
            y += Block(g, h, F(9), Ink, m + 16, y, w - 16, 1.3) + 5;
        }

        double? infoY = null;
        if (d.InfoUrl is { } info)
        {
            y += 8;
            infoY = y;
            g.DrawRoundedRectangle(accentPen, XBrushes.White, m, y, w, InfoBoxH, 10, 10);
            Text(g, T("infoTitle"), F(9.5, true), Accent, m + 16, y + 9, w - 32, 14);
            var shown = info.Split('?')[0].Replace("https://", "").Replace("http://", "");
            Text(g, shown + "  →", F(10), Ink, m + 16, y + 25, w - 32, 14);
            y += InfoBoxH;
        }

        var closing = T("closing");
        if (!LetterTemplate.IsOmitted(closing) && y < 770) Block(g, closing, F(9.5), Ink, m, y + 14, w, 1.3);

        g.DrawLine(linePen, m, 800, m + w, 800);
        Text(g, $"{d.OrganizationName} · {d.TenantName} · {Brand.Footer ?? "Erstellt mit dem Yubikey Lifecycle Manager."} Die PIN wurde nicht gespeichert.",
            F(7.5), Muted, m, 806, w, 12);
        return infoY;
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

    private static List<string> Wrap(XGraphics g, string text, XFont font, double w)
    {
        var lines = new List<string>();
        foreach (var para in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && g.MeasureString(candidate, font).Width > w) { lines.Add(line); line = word; }
                else line = candidate;
            }
            lines.Add(line);
        }
        return lines;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void DrawPin(XGraphics g, string pin, double x, double y, double maxW)
    {
        var cell = Math.Min(30, maxW / Math.Max(pin.Length, 1));
        var font = F(Math.Min(24, cell * 0.72), true);
        for (var i = 0; i < pin.Length; i++)
        {
            var cx = x + i * cell;
            g.DrawRoundedRectangle(new XPen(Mix(Accent, 0.45), 0.9), XBrushes.White, cx, y, cell - 5, 36, 6, 6);
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
        g.DrawString("Anmelden", F(7, true), new XSolidBrush(Ink), win.X + 26, win.Y + 16);
        g.DrawRectangle(new XSolidBrush(Line), win.X + 8, win.Y + 26, win.Width - 16, 7);
        var btn = new XRect(win.X + 8, win.Y + 40, win.Width - 16, 18);
        g.DrawRoundedRectangle(new XPen(Accent, 1), new XSolidBrush(AccentSoft), btn.X, btn.Y, btn.Width, btn.Height, 3, 3);
        DrawKey(g, btn.X + 4, btn.Y + 5, 18, 8, false, false);
        g.DrawString("Sicherheitsschlüssel", F(5.4, true), new XSolidBrush(Accent), btn.X + 26, btn.Y + 12);
    }

    private static void DrawPinEntry(XGraphics g, XRect a)
    {
        var box = new XRect(a.X + 10, a.Y + a.Height * 0.32, a.Width - 20, 22);
        g.DrawString("Sicherheitsschlüssel-PIN", F(7, true), new XSolidBrush(Ink), a.X + 10, a.Y + a.Height * 0.25);
        g.DrawRoundedRectangle(new XPen(Accent, 1), XBrushes.White, box.X, box.Y, box.Width, box.Height, 3, 3);
        for (var i = 0; i < 6; i++) g.DrawEllipse(new XSolidBrush(Muted), box.X + 8 + i * 9, box.Y + 8, 5, 5);
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
