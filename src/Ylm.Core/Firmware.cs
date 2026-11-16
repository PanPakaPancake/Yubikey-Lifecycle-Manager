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

public sealed record FirmwareAdvisory(string Id, string Title, string Severity, string Url, FirmwareVersion? FromInclusive, FirmwareVersion ToExclusive, bool AffectsFido);

public static class FirmwareAdvisories
{
    public static readonly IReadOnlyList<FirmwareAdvisory> Known =
    [
        new("YSA-2024-03", "EUCLEAK: Seitenkanal in der ECDSA-Implementierung (Infineon)", "mittel",
            "https://www.yubico.com/support/security-advisories/ysa-2024-03/", null, new FirmwareVersion(5, 7, 0), true),
        new("YSA-2017-01", "ROCA: schwache RSA-Schlüssel (betrifft PIV/OpenPGP, nicht FIDO2)", "info",
            "https://www.yubico.com/support/security-advisories/ysa-2017-01/", new FirmwareVersion(4, 2, 6), new FirmwareVersion(4, 3, 5), false),
    ];

    public static IReadOnlyList<FirmwareAdvisory> For(string? firmware)
    {
        if (!FirmwareVersion.TryParse(firmware, out var v)) return [];
        return Known.Where(a => (a.FromInclusive is null || v >= a.FromInclusive.Value) && v < a.ToExclusive).ToList();
    }
}
