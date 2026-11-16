using System.Security.Cryptography;
using System.Text;
using Ylm.Contracts;

namespace Ylm.Core;

public enum PinStrategy
{
    /// <summary>Zufällige PIN pro Key (Standard).</summary>
    Random = 0,
    /// <summary>Feste Standard-PIN aus dem Profil.</summary>
    Fixed = 1,
}

/// <summary>The settings of a registration profile that matter for evaluation (planning 6.1).</summary>
public sealed record ProfileSettings
{
    public PinStrategy PinStrategy { get; init; } = PinStrategy.Random;
    /// <summary>Length of generated PINs; minimum length for a fixed PIN.</summary>
    public int PinLength { get; init; } = 8;
    public bool ForcePinChange { get; init; } = true;
    public bool FactoryResetBeforeRegistration { get; init; }
    public string DisplayNameTemplate { get; init; } = "YubiKey {Seriennummer}";
    /// <summary>Allowed models as substrings of the model name, e.g. "YubiKey 5". Empty = all.</summary>
    public IReadOnlyList<string> AllowedModels { get; init; } = [];
    public string? MinimumFirmware { get; init; }
}

public enum ViolationCode
{
    NoFido2,
    ModelNotAllowed,
    FirmwareBelowMinimum,
    ForcePinChangeUnsupported,
    PinAlreadySet,
    NoCredentialSpace,
    PinTooShortForKey,
    AaguidNotAllowedByTenant,
}

public sealed record ProfileViolation(ViolationCode Code, string Message, bool CanOverride, bool ResolvedByReset);

public sealed record EvaluationResult(IReadOnlyList<ProfileViolation> Violations)
{
    public bool Compatible => Violations.Count == 0;

    /// <summary>
    /// Checks whether the registration may proceed given the explicit overrides confirmed by the admin
    /// and whether a factory reset is going to happen.
    /// </summary>
    public IReadOnlyList<ProfileViolation> Blocking(IReadOnlyCollection<ViolationCode> overrides, bool resetPlanned) =>
        Violations.Where(v => !(resetPlanned && v.ResolvedByReset) && !(v.CanOverride && overrides.Contains(v.Code))).ToList();
}

public static class ProfileEvaluator
{
    public static EvaluationResult Evaluate(ProfileSettings profile, KeyInfo key)
    {
        var list = new List<ProfileViolation>();

        if (!key.HasFido2)
        {
            list.Add(new(ViolationCode.NoFido2, "Auf diesem Key ist die FIDO2-Funktion nicht verfügbar oder deaktiviert.", false, false));
            return new EvaluationResult(list);
        }

        if (profile.AllowedModels.Count > 0)
        {
            var model = key.Model ?? "";
            if (!profile.AllowedModels.Any(m => model.Contains(m, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new(ViolationCode.ModelNotAllowed,
                    $"Modell „{(model.Length > 0 ? model : "unbekannt")}“ ist im Profil nicht erlaubt (erlaubt: {string.Join(", ", profile.AllowedModels)}).",
                    true, false));
            }
        }

        FirmwareVersion.TryParse(key.FirmwareVersion, out var fw);
        if (FirmwareVersion.TryParse(profile.MinimumFirmware, out var minFw) && fw < minFw)
        {
            list.Add(new(ViolationCode.FirmwareBelowMinimum,
                $"Firmware {key.FirmwareVersion} liegt unter der im Profil geforderten Mindestversion {minFw}.", true, false));
        }

        if (profile.ForcePinChange && !key.SupportsForcePinChange)
        {
            list.Add(new(ViolationCode.ForcePinChangeUnsupported,
                $"Dieser Key (Firmware {key.FirmwareVersion}) kann keinen PIN-Wechsel erzwingen. Das geht erst ab Firmware {FirmwareVersion.ForcePinChangeMinimum}. " +
                "Wenn Sie fortfahren, steht im PIN-PDF „Bitte ändern Sie Ihre PIN selbst“.", true, false));
        }

        if (key.PinIsSet && !profile.FactoryResetBeforeRegistration)
        {
            list.Add(new(ViolationCode.PinAlreadySet,
                "Auf dem Key ist bereits eine FIDO2-PIN gesetzt. Für die Registrierung ist ein Werksreset der FIDO2-Funktion nötig.", false, true));
        }

        if (key.RemainingDiscoverableCredentials is 0 && !profile.FactoryResetBeforeRegistration)
        {
            list.Add(new(ViolationCode.NoCredentialSpace,
                "Auf dem Key ist kein Platz für einen weiteren Passkey. Ein Werksreset schafft Platz.", false, true));
        }

        var keyMin = key.MinimumPinLength ?? 4;
        if (profile.PinLength < keyMin)
        {
            list.Add(new(ViolationCode.PinTooShortForKey,
                $"Der Key verlangt mindestens {keyMin} Stellen, das Profil erzeugt nur {profile.PinLength}.", false, false));
        }

        return new EvaluationResult(list);
    }
}

public static class PinGenerator
{
    public const int MinLength = 4;
    public const int MaxLength = 63;

    /// <summary>Generates a random numeric PIN that avoids trivial patterns.</summary>
    public static string Generate(int length)
    {
        if (length is < MinLength or > MaxLength) throw new ArgumentOutOfRangeException(nameof(length));
        while (true)
        {
            var sb = new StringBuilder(length);
            for (var i = 0; i < length; i++) sb.Append((char)('0' + RandomNumberGenerator.GetInt32(10)));
            var pin = sb.ToString();
            if (!IsTrivial(pin)) return pin;
        }
    }

    public static bool IsTrivial(string pin)
    {
        if (pin.Length == 0) return true;
        if (pin.Distinct().Count() <= 2) return true; // 1111, 1212, 1122 ...
        bool asc = true, desc = true;
        for (var i = 1; i < pin.Length; i++)
        {
            if (pin[i] != pin[i - 1] + 1) asc = false;
            if (pin[i] != pin[i - 1] - 1) desc = false;
        }
        if (asc || desc) return true;
        // repeating period up to 3, e.g. 123123
        for (var period = 1; period <= 3 && period < pin.Length; period++)
        {
            var repeats = true;
            for (var i = period; i < pin.Length; i++)
            {
                if (pin[i] != pin[i - period]) { repeats = false; break; }
            }
            if (repeats) return true;
        }
        return false;
    }

    public static string? ValidateFixedPin(string pin, int minLength)
    {
        if (pin.Length < Math.Max(minLength, MinLength)) return $"Die PIN muss mindestens {Math.Max(minLength, MinLength)} Zeichen haben.";
        if (pin.Length > MaxLength) return $"Die PIN darf höchstens {MaxLength} Zeichen haben.";
        if (IsTrivial(pin)) return "Die PIN ist zu einfach (Wiederholungen oder Zahlenfolgen).";
        return null;
    }
}

public static class DisplayNameTemplate
{
    public const int MaxLength = 64;

    public static string Render(string template, KeyInfo key, string? userPrincipalName, DateOnly date)
    {
        var serial = key.SerialNumber?.ToString() ?? "ohne Seriennummer";
        var result = (string.IsNullOrWhiteSpace(template) ? "YubiKey {Seriennummer}" : template)
            .Replace("{Seriennummer}", serial, StringComparison.OrdinalIgnoreCase)
            .Replace("{Serial}", serial, StringComparison.OrdinalIgnoreCase)
            .Replace("{Modell}", key.Model ?? "YubiKey", StringComparison.OrdinalIgnoreCase)
            .Replace("{Model}", key.Model ?? "YubiKey", StringComparison.OrdinalIgnoreCase)
            .Replace("{Firmware}", key.FirmwareVersion, StringComparison.OrdinalIgnoreCase)
            .Replace("{Benutzer}", userPrincipalName ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{UPN}", userPrincipalName ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{Datum}", date.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Trim();
        return result.Length > MaxLength ? result[..MaxLength] : result;
    }
}

public static class KeyModelNames
{
    /// <summary>Builds a readable model name from what the SDK reports.</summary>
    public static string Describe(string? formFactor, bool hasNfc, bool isSecurityKeySeries, bool isFips, int firmwareMajor)
    {
        var ff = formFactor ?? "";
        if (ff.Contains("Bio", StringComparison.OrdinalIgnoreCase))
            return ff.Contains("UsbC", StringComparison.OrdinalIgnoreCase) ? "YubiKey C Bio" : "YubiKey Bio";

        string baseName;
        if (isSecurityKeySeries)
        {
            baseName = ff.Contains("UsbC", StringComparison.OrdinalIgnoreCase) ? "Security Key C" : "Security Key";
            return hasNfc ? baseName + " NFC" : baseName;
        }

        var series = firmwareMajor >= 5 ? "5" : firmwareMajor.ToString();
        baseName = ff switch
        {
            _ when ff.Contains("Lightning", StringComparison.OrdinalIgnoreCase) => $"YubiKey {series}Ci",
            _ when ff.Equals("UsbCNano", StringComparison.OrdinalIgnoreCase) => $"YubiKey {series}C Nano",
            _ when ff.Equals("UsbANano", StringComparison.OrdinalIgnoreCase) => $"YubiKey {series} Nano",
            _ when ff.Contains("UsbC", StringComparison.OrdinalIgnoreCase) => $"YubiKey {series}C",
            _ => $"YubiKey {series}",
        };
        if (hasNfc) baseName += " NFC";
        if (isFips) baseName += " FIPS";
        return baseName;
    }
}
