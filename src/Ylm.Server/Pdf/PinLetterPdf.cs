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
        baseUrl is null ? null : $"{baseUrl}/passkey/anleitung" + (tenantId is { } t ? $"?t={t}" : "");

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

    /// <returns>Oberkante des Hinweises auf die Infoseite, falls gezeichnet.</returns>
    private static double? Draw(XGraphics g, PinLetterData d, double pageWidth)
    {
        const double m = 48;
        var w = pageWidth - 2 * m;

        // Kopf
        g.DrawRectangle(new XSolidBrush(Accent), 0, 0, pageWidth, 8);
        var hasLogo = DrawLogo(g, m + w, 20, 150, 40);
        Text(g, d.OrganizationName, F(10, true), Muted, m, 30, hasLogo ? w - 160 : w, 14);
        Text(g, "Ihr YubiKey und Ihre PIN", F(22, true), Ink, m, 46, w, 30);
        Text(g, d.SharedAccounts is { Count: > 0 }
                ? $"Für den Verwahrer {d.UserDisplayName} · {d.UserPrincipalName} für {d.SharedAccounts.Count} Konto/Konten: {Shorten(string.Join(", ", d.SharedAccounts), 110)}"
                : $"Persönlich für {d.UserDisplayName} ({d.UserPrincipalName})", F(11), Ink, m, 80, w, 16);
        Text(g, $"{d.KeyModel}{(d.SerialNumber is { } s ? $" · Seriennummer {s}" : "")} · erstellt am {d.CreatedAt:dd.MM.yyyy}", F(9.5), Muted, m, 97, w, 14);

        // PIN-Kasten
        double boxY = 124, boxH = 104;
        g.DrawRoundedRectangle(new XPen(Accent, 1.2), new XSolidBrush(AccentSoft), m, boxY, w, boxH, 10, 10);
        Text(g, "Ihre persönliche PIN", F(10, true), Accent, m + 18, boxY + 14, 200, 14);
        DrawPin(g, d.Pin, m + 18, boxY + 36, w - 36);
        Text(g, d.PinChangeForced
                ? "Bei der ersten Verwendung werden Sie aufgefordert, eine neue PIN zu wählen."
                : "Bitte ändern Sie Ihre PIN selbst, sobald Sie den Key erhalten haben (siehe unten).",
            F(9.5), Ink, m + 18, boxY + 80, w - 36, 14);

        // Anleitung
        var y = boxY + boxH + 26;
        Text(g, "So melden Sie sich mit dem YubiKey an", F(14, true), Ink, m, y, w, 20);
        y += 28;
        var colW = (w - 3 * 12) / 4;
        string[] titles = ["1  Key einstecken", "2  Option wählen", "3  PIN eingeben", "4  Key berühren"];
        string[] texts =
        [
            "Stecken Sie den YubiKey in einen freien USB-Anschluss Ihres Geräts.",
            "Bei der Microsoft-Anmeldung „Anmeldeoptionen“ bzw. „Gesicht, Fingerabdruck, PIN oder Sicherheitsschlüssel“ wählen.",
            d.PinChangeForced
                ? "Geben Sie die PIN oben ein. Beim ersten Mal legen Sie danach eine neue, eigene PIN fest."
                : "Geben Sie Ihre PIN ein, wenn Windows oder der Browser danach fragt.",
            "Berühren Sie die goldene Fläche, sobald der Key blinkt. Fertig.",
        ];
        for (var i = 0; i < 4; i++)
        {
            var x = m + i * (colW + 12);
            g.DrawRoundedRectangle(new XPen(Line, 0.8), XBrushes.White, x, y, colW, 210, 8, 8);
            var art = new XRect(x + 8, y + 8, colW - 16, 92);
            g.DrawRoundedRectangle(new XSolidBrush(XColor.FromArgb(0xf4, 0xf6, 0xf9)), art.X, art.Y, art.Width, art.Height, 6, 6);
            switch (i)
            {
                case 0: DrawPlugIn(g, art); break;
                case 1: DrawSignInOptions(g, art); break;
                case 2: DrawPinEntry(g, art); break;
                default: DrawTouch(g, art); break;
            }
            Text(g, titles[i], F(10, true), Ink, x + 10, y + 110, colW - 20, 28);
            Text(g, texts[i], F(8.6), Muted, x + 10, y + 128, colW - 20, 80);
        }
        y += 228;

        if (!d.PinChangeForced)
        {
            g.DrawRoundedRectangle(new XPen(Warn, 1), new XSolidBrush(WarnSoft), m, y, w, 62, 8, 8);
            Text(g, "Bitte PIN selbst ändern", F(10.5, true), Warn, m + 14, y + 10, w - 28, 14);
            Text(g, "Windows: Einstellungen → Konten → Anmeldeoptionen → Sicherheitsschlüssel → Verwalten → Sicherheitsschlüssel-PIN „Ändern“. " +
                    "Wählen Sie eine PIN, die nur Sie kennen.", F(9), Ink, m + 14, y + 27, w - 28, 32);
            y += 76;
        }

        // Hinweise
        Text(g, "Wichtig", F(11, true), Ink, m, y, w, 16);
        y += 18;
        string[] hints =
        [
            "Geben Sie Ihre PIN niemals weiter. Die IT wird Sie nie danach fragen.",
            "Nach 8 Fehleingaben wird der Key gesperrt. Wenden Sie sich dann an die IT" + (Brand.Contact is { } c ? $": {c}." : "."),
            "Key verloren oder gestohlen? Melden Sie es sofort der IT, damit er gesperrt wird.",
            "Bewahren Sie diesen Brief nicht zusammen mit dem Key auf und vernichten Sie ihn, sobald Sie die PIN geändert haben.",
        ];
        foreach (var h in hints)
        {
            g.DrawEllipse(new XSolidBrush(Accent), m + 2, y + 5, 4, 4);
            Text(g, h, F(9), Ink, m + 14, y, w - 14, 26);
            y += 17;
        }

        double? infoY = null;
        if (d.InfoUrl is { } info)
        {
            y += 10;
            infoY = y;
            g.DrawRoundedRectangle(new XPen(Accent, 0.8), new XSolidBrush(AccentSoft), m, y, w, InfoBoxH, 8, 8);
            Text(g, "Was ist ein Passkey? Anleitung und Erklärung:", F(10, true), Accent, m + 14, y + 9, w - 28, 14);
            var shown = info.Split('?')[0].Replace("https://", "").Replace("http://", "");
            Text(g, shown, F(10), Ink, m + 14, y + 25, w - 28, 14);
        }

        g.DrawLine(new XPen(Line, 0.6), m, 800, m + w, 800);
        Text(g, $"{d.OrganizationName} · {d.TenantName} · {Brand.Footer ?? "Erstellt mit dem Yubikey Lifecycle Manager."} Die PIN wurde nicht gespeichert.",
            F(7.5), Muted, m, 806, w, 12);
        return infoY;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static void DrawPin(XGraphics g, string pin, double x, double y, double maxW)
    {
        var cell = Math.Min(34, maxW / Math.Max(pin.Length, 1));
        var font = F(Math.Min(24, cell * 0.72), true);
        for (var i = 0; i < pin.Length; i++)
        {
            var cx = x + i * cell;
            g.DrawRoundedRectangle(new XPen(Line, 0.8), XBrushes.White, cx, y, cell - 5, 36, 5, 5);
            g.DrawString(pin[i].ToString(), font, new XSolidBrush(Ink), new XRect(cx, y, cell - 5, 36), XStringFormats.Center);
        }
    }

    private static void DrawKey(XGraphics g, double x, double y, double len, double h, bool vertical, bool glow)
    {
        // Simple YubiKey: dark body, gold touch disc, metal USB connector.
        if (vertical)
        {
            g.DrawRectangle(new XSolidBrush(XColor.FromArgb(0xb8, 0xbe, 0xc7)), x + h * 0.22, y + len * 0.72, h * 0.56, len * 0.28);
            g.DrawRoundedRectangle(new XSolidBrush(KeyBody), x, y, h, len * 0.74, h * 0.4, h * 0.4);
            var cx = x + h / 2; var cy = y + len * 0.3;
            if (glow) for (var r = 3; r >= 1; r--) g.DrawEllipse(new XPen(XColor.FromArgb(70 * r, Gold), 1), cx - h * 0.25 - r * 4, cy - h * 0.25 - r * 4, h * 0.5 + r * 8, h * 0.5 + r * 8);
            g.DrawEllipse(new XSolidBrush(Gold), cx - h * 0.25, cy - h * 0.25, h * 0.5, h * 0.5);
            g.DrawEllipse(new XPen(XColor.FromArgb(0x55, 0x5b, 0x66), 1), cx - h * 0.12, y + 4, h * 0.24, h * 0.24);
        }
        else
        {
            g.DrawRectangle(new XSolidBrush(XColor.FromArgb(0xb8, 0xbe, 0xc7)), x + len * 0.72, y + h * 0.22, len * 0.28, h * 0.56);
            g.DrawRoundedRectangle(new XSolidBrush(KeyBody), x, y, len * 0.74, h, h * 0.4, h * 0.4);
            var cx = x + len * 0.3; var cy = y + h / 2;
            g.DrawEllipse(new XSolidBrush(Gold), cx - h * 0.25, cy - h * 0.25, h * 0.5, h * 0.5);
        }
    }

    private static void DrawPlugIn(XGraphics g, XRect a)
    {
        // Laptop side with a USB port, key moving in.
        var baseY = a.Y + a.Height * 0.62;
        g.DrawRoundedRectangle(new XSolidBrush(XColor.FromArgb(0xc9, 0xcf, 0xd8)), a.X + a.Width * 0.62, baseY - 16, a.Width * 0.34, 30, 4, 4);
        g.DrawRectangle(new XSolidBrush(XColor.FromArgb(0x4a, 0x50, 0x5a)), a.X + a.Width * 0.62, baseY - 6, 6, 10);
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
        for (var i = 0; i < 6; i++) g.DrawEllipse(new XSolidBrush(Ink), box.X + 8 + i * 9, box.Y + 8, 5, 5);
        g.DrawRoundedRectangle(new XSolidBrush(Accent), a.X + a.Width - 48, box.Y + 30, 38, 15, 3, 3);
        g.DrawString("OK", F(7, true), XBrushes.White, new XRect(a.X + a.Width - 48, box.Y + 30, 38, 15), XStringFormats.Center);
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
