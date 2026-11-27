using System.Globalization;

namespace Ylm.Core;

public readonly record struct FirmwareVersion(int Major, int Minor, int Patch) : IComparable<FirmwareVersion>
{
    public static FirmwareVersion Parse(string value) =>
        TryParse(value, out var v) ? v : throw new FormatException($"Ungültige Firmware-Version: {value}");

    public static bool TryParse(string? value, out FirmwareVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split('.');
        if (parts.Length is < 2 or > 3) return false;
        var nums = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i])) return false;
        }
        version = new FirmwareVersion(nums[0], nums[1], nums[2]);
        return true;
    }

    public int CompareTo(FirmwareVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public static bool operator <(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(FirmwareVersion a, FirmwareVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";

    /// <summary>First firmware with CTAP 2.1 forcePINChange (planning 6.1).</summary>
    public static readonly FirmwareVersion ForcePinChangeMinimum = new(5, 7, 0);
}

/// <summary>Produktreihe eines Keys, wie sie Yubico in Sicherheitshinweisen unterscheidet.</summary>
public enum KeySeries { Unknown, YubiKey5, YubiKey5Fips, YubiKey5Cspn, YubiKeyBio, SecurityKey, YubiKey4, YubiKey4Fips, Neo }

public static class KeySeriesInfo
{
    public static string DisplayName(KeySeries s) => s switch
    {
        KeySeries.YubiKey5 => "YubiKey 5",
        KeySeries.YubiKey5Fips => "YubiKey 5 FIPS",
        KeySeries.YubiKey5Cspn => "YubiKey 5 CSPN",
        KeySeries.YubiKeyBio => "YubiKey Bio",
        KeySeries.SecurityKey => "Security Key",
        KeySeries.YubiKey4 => "YubiKey 4",
        KeySeries.YubiKey4Fips => "YubiKey 4 FIPS",
        KeySeries.Neo => "YubiKey NEO",
        _ => "unbekannt",
    };

    /// <summary>
    /// Reihe aus Modellname (vom Agent oder CSV-Import), FIPS-Kennzeichen und Firmware.
    /// Ohne Modellname entscheidet die Hauptversion der Firmware (5.x = YubiKey 5, 4.x = YubiKey 4).
    /// </summary>
    public static KeySeries Detect(string? model, bool isFips, string? firmware)
    {
        var m = model ?? "";
        var major = FirmwareVersion.TryParse(firmware, out var v) ? v.Major : 0;
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("Bio")) return KeySeries.YubiKeyBio;
        if (Has("Security Key")) return KeySeries.SecurityKey;
        if (Has("CSPN")) return KeySeries.YubiKey5Cspn;
        if (Has("NEO")) return KeySeries.Neo;
        var fips = isFips || Has("FIPS");
        if (major == 0)
        {
            if (Has("YubiKey 5") || Has("YubiKey 5C")) major = 5;
            else if (Has("YubiKey 4")) major = 4;
        }
        return major switch
        {
            >= 5 => fips ? KeySeries.YubiKey5Fips : KeySeries.YubiKey5,
            4 => fips ? KeySeries.YubiKey4Fips : KeySeries.YubiKey4,
            3 => KeySeries.Neo,
            _ => KeySeries.Unknown,
        };
    }
}

/// <summary>Ein betroffener Firmware-Bereich: Reihe(n), ab (einschließlich), bis (ausschließlich), optional nur Modelle mit diesem Namensteil.</summary>
public sealed record AdvisoryRange(KeySeries[] Series, FirmwareVersion? FromInclusive, FirmwareVersion ToExclusive, string? ModelContains = null)
{
    public bool Matches(KeySeries series, string? model, FirmwareVersion v) =>
        Series.Contains(series)
        && (FromInclusive is null || v >= FromInclusive.Value)
        && v < ToExclusive
        && (ModelContains is null || (model ?? "").Contains(ModelContains, StringComparison.OrdinalIgnoreCase));

    public string Describe()
    {
        var who = string.Join(", ", Series.Select(KeySeriesInfo.DisplayName)) + (ModelContains is null ? "" : $" (nur {ModelContains})");
        var to = new FirmwareVersion(ToExclusive.Major, ToExclusive.Minor, Math.Max(0, ToExclusive.Patch - 1));
        if (FromInclusive is null) return $"{who} vor {ToExclusive}";
        if (ToExclusive.Patch == 0) return $"{who} ab {FromInclusive} bis vor {ToExclusive}";
        return FromInclusive.Value == to ? $"{who} {to}" : $"{who} {FromInclusive} bis {to}";
    }
}

/// <param name="Severity">Einstufung durch Yubico: hoch, mittel, niedrig oder „nicht angegeben“.</param>
/// <param name="AffectsFido">Betrifft FIDO2/U2F, also die Passkeys in Entra. Andere Hinweise betreffen nur PIV, OpenPGP oder OTP.</param>
/// <param name="Applications">Betroffene Anwendungen auf dem Key, in Alltagssprache.</param>
/// <param name="Summary">Worum es geht, in ein bis zwei Sätzen.</param>
/// <param name="Recommendation">Was ein Admin tun sollte.</param>
/// <param name="FixedIn">Ab welcher Firmware behoben (Firmware auf YubiKeys lässt sich nicht aktualisieren, also: Key tauschen).</param>
public sealed record FirmwareAdvisory(
    string Id, string Title, string Severity, string Url, DateOnly Published, bool AffectsFido,
    string Applications, string Summary, string Recommendation, string FixedIn, IReadOnlyList<AdvisoryRange> Ranges)
{
    public bool Matches(KeySeries series, string? model, string? firmware) =>
        FirmwareVersion.TryParse(firmware, out var v) && Ranges.Any(r => r.Matches(series, model, v));
}

/// <summary>
/// Gepflegte Liste der Yubico-Sicherheitshinweise, die die Firmware von YubiKeys betreffen
/// (Quelle: https://www.yubico.com/support/security-advisories/). Hinweise zu Software wie YubiKey Manager,
/// libfido2 oder pam-u2f fehlen absichtlich, weil sie nichts mit dem einzelnen Key zu tun haben.
/// Neuen Hinweis hinzufügen: Eintrag unten ergänzen (Bereiche je Reihe, „bis“ jeweils ausschließlich),
/// Test in CoreTests ergänzen, Changelog-Eintrag schreiben.
/// </summary>
public static class FirmwareAdvisories
{
    private static FirmwareVersion V(int a, int b, int c = 0) => new(a, b, c);
    private static readonly KeySeries[] Five = [KeySeries.YubiKey5];
    private static readonly KeySeries[] FiveAll = [KeySeries.YubiKey5, KeySeries.YubiKey5Fips, KeySeries.YubiKey5Cspn, KeySeries.SecurityKey];

    public static readonly IReadOnlyList<FirmwareAdvisory> Known =
    [
        new("YSA-2025-02", "PIN/UV-Protokoll 2 weicht vom FIDO-Standard ab", "niedrig",
            "https://www.yubico.com/support/security-advisories/ysa-2025-02/", new(2025, 4, 2), true,
            "FIDO2 (Passkeys)",
            "Bei PIN/UV-Protokoll 2 verhält sich die Firmware nicht ganz standardgemäß (CVE-2025-29991). Yubico stuft das Risiko als gering ein.",
            "Kein Tausch nötig. Bei Neubeschaffung auf Firmware 5.7.4 oder neuer achten.",
            "5.7.4",
            [
                new([KeySeries.YubiKey5, KeySeries.YubiKey5Fips, KeySeries.SecurityKey], V(5, 4, 1), V(5, 7, 4)),
                new([KeySeries.YubiKey5Cspn], V(5, 4, 2), V(5, 4, 3)),
                new([KeySeries.YubiKeyBio], V(5, 5, 0), V(5, 7, 4)),
            ]),
        new("YSA-2024-03", "EUCLEAK: Seitenkanal in der ECDSA-Implementierung (Infineon)", "mittel",
            "https://www.yubico.com/support/security-advisories/ysa-2024-03/", new(2024, 9, 3), true,
            "FIDO2 (Passkeys), PIV und OpenPGP mit ECC-Schlüsseln",
            "Wer den Key längere Zeit in der Hand hat und teure Spezialausrüstung besitzt, kann einen privaten Schlüssel rekonstruieren und den Key klonen. Für einen Angriff braucht es zusätzlich das Konto und in der Regel die PIN.",
            "Firmware lässt sich nicht aktualisieren. Keys schrittweise gegen Firmware 5.7 oder neuer tauschen, zuerst für Admin- und Breakglass-Konten. Verlorene Keys sofort sperren.",
            "5.7.0 (YubiKey Bio: 5.7.2)",
            [
                new(FiveAll, null, V(5, 7, 0)),
                new([KeySeries.YubiKeyBio], null, V(5, 7, 2)),
            ]),
        new("YSA-2024-02", "Auflisten der Dienste mit gespeichertem Passkey", "niedrig",
            "https://www.yubico.com/support/security-advisories/ysa-2024-02/", new(2024, 5, 21), true,
            "FIDO2 (nur gespeicherte Passkeys, nicht U2F)",
            "Wer den Key in der Hand hat, kann unter Umständen auslesen, für welche Dienste Passkeys darauf gespeichert sind (CVE-2024-35311). Anmelden kann er sich damit nicht.",
            "Kein Tausch nötig. Verlorene Keys wie immer sofort sperren.",
            "5.7.0 (YubiKey Bio: 5.6.4, YubiKey 5 FIPS: 5.7.2)",
            [
                new([KeySeries.YubiKey5, KeySeries.SecurityKey, KeySeries.YubiKey5Fips], null, V(5, 4, 4)),
                new([KeySeries.YubiKeyBio], null, V(5, 6, 4)),
                new([KeySeries.YubiKey5Cspn], V(5, 4, 2), V(5, 4, 3)),
            ]),
        new("YSA-2020-05", "OpenPGP: Fehler beim Resetting Code", "mittel",
            "https://www.yubico.com/support/security-advisories/ysa-2020-05/", new(2020, 7, 8), false,
            "nur OpenPGP",
            "In der OpenPGP-Anwendung ließ sich die PIN mit einem nicht gesetzten Resetting Code zurücksetzen (CVE-2020-15000). Passkeys sind nicht betroffen.",
            "Für Entra ohne Bedeutung. Wer OpenPGP nutzt: Resetting Code setzen und Berührung erzwingen.",
            "5.2.7",
            [new(Five, V(5, 2, 0), V(5, 2, 7))]),
        new("YSA-2020-04", "NFC: Zugangscode beim Ändern der NDEF-Daten nicht geprüft", "mittel",
            "https://www.yubico.com/support/security-advisories/ysa-2020-04/", new(2020, 7, 8), false,
            "nur OTP über NFC",
            "Über NFC ließ sich die OTP-Konfiguration ohne Zugangscode ändern (CVE-2020-15001). Nur YubiKey 5 NFC, Passkeys sind nicht betroffen.",
            "Für Entra ohne Bedeutung. Wer OTP nutzt: OTP über NFC abschalten oder statisches Passwort neu setzen.",
            "5.2.7 bzw. 5.3.2",
            [new(Five, V(5, 0, 0), V(5, 2, 7), "NFC"), new(Five, V(5, 3, 0), V(5, 3, 2), "NFC")]),
        new("YSA-2019-02", "YubiKey FIPS: wenig Zufall direkt nach dem Einstecken", "nicht angegeben",
            "https://www.yubico.com/support/security-advisories/ysa-2019-02/", new(2019, 6, 13), true,
            "FIDO U2F, PIV, OpenPGP, OATH",
            "Die ersten Zufallswerte nach dem Einstecken waren schwächer als vorgesehen. Yubico hat die FIPS-Zulassung dieser Firmware zurückgezogen und die Keys getauscht.",
            "Keys austauschen.",
            "4.4.5",
            [new([KeySeries.YubiKey4Fips], V(4, 4, 2), V(4, 4, 5))]),
        new("YSA-2017-01", "ROCA: schwache RSA-Schlüssel (betrifft PIV/OpenPGP, nicht FIDO2)", "nicht angegeben",
            "https://www.yubico.com/support/security-advisories/ysa-2017-01/", new(2017, 10, 16), false,
            "nur RSA-Schlüssel aus PIV und OpenPGP",
            "Auf dem Key erzeugte RSA-Schlüssel sind zu schwach (CVE-2017-15361). FIDO2/U2F ist nicht betroffen.",
            "Für Entra ohne Bedeutung. Auf dem Key erzeugte RSA-Schlüssel ersetzen oder Key tauschen.",
            "4.3.5",
            [new([KeySeries.YubiKey4], V(4, 2, 6), V(4, 3, 5))]),
    ];

    public static FirmwareAdvisory? Find(string? id) => Known.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Hinweise für einen Key mit Modell und FIPS-Kennzeichen.</summary>
    public static IReadOnlyList<FirmwareAdvisory> For(string? firmware, string? model, bool isFips = false)
    {
        if (!FirmwareVersion.TryParse(firmware, out _)) return [];
        var series = KeySeriesInfo.Detect(model, isFips, firmware);
        return Known.Where(a => a.Matches(series, model, firmware)).ToList();
    }

    /// <summary>Nur aus der Firmware (Modell unbekannt): 5.x gilt als YubiKey 5, 4.x als YubiKey 4.</summary>
    public static IReadOnlyList<FirmwareAdvisory> For(string? firmware) => For(firmware, null);
}
