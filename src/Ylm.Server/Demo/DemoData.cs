using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Graph;

namespace Ylm.Server.Demo;

/// <summary>Physische Daten eines Demo-Keys, der in Entra als FIDO2-Methode einer Person registriert ist.</summary>
public sealed record DemoKeySpec(int Serial, string Firmware, string Model, string FormFactor, KeyUsage Usage = KeyUsage.Personal, string? Custodian = null);

/// <summary>
/// Erfundene Beispieldaten des Demo-Modus. Alles ist fest vorgegeben (gleicher Zufalls-Startwert), damit Screenshots
/// und Anleitungen zu jeder Demo passen. Domains unter .example sind reserviert und gehören niemandem.
/// </summary>
public static class DemoData
{
    public static readonly Guid ContosoTenantId = Guid.Parse("d3e0c0a1-0000-4000-8000-000000000001");
    public static readonly Guid FabrikamTenantId = Guid.Parse("d3e0c0a1-0000-4000-8000-000000000002");

    public static readonly Guid Aaguid57 = Guid.Parse("19083c3d-8383-4b18-bc03-8f1c9ab2fd1b");
    public static readonly Guid Aaguid57Nfc = Guid.Parse("a25342c0-3cdc-4414-8e46-f4807fca511c");
    public static readonly Guid Aaguid54 = Guid.Parse("ee882879-721c-4913-9775-3dfcce97072a");
    public static readonly Guid Aaguid54Nfc = Guid.Parse("2fc0579f-8113-47ea-b116-bb5a8db9202a");
    public static readonly Guid Aaguid5Ci = Guid.Parse("c5ef55ff-ad9a-4b9f-b580-adebafe026d0");
    private static readonly Guid AuthenticatorAaguid = Guid.Parse("90a3ccdf-635c-4729-a248-9b709135078f");

    public const string GroupYubiKey = "demo-grp-yubikey";
    public const string GroupProduction = "demo-grp-produktion";
    public const string GroupBreakglass = "demo-grp-breakglass";

    /// <summary>Methoden-ID → Seriennummer, Firmware, Modell. Methoden ohne Eintrag kennt die Plattform nur aus dem Sync.</summary>
    public static Dictionary<string, DemoKeySpec> KeySpecs { get; } = [];

    private static readonly string[] FirstNames =
    [
        "Anna", "Ben", "Clara", "David", "Elena", "Felix", "Greta", "Hannes", "Ida", "Jonas", "Klara", "Lukas", "Mara", "Noah",
        "Olivia", "Paul", "Rosa", "Samuel", "Tilda", "Valentin", "Wiebke", "Yusuf", "Zoe", "Emil", "Lea", "Moritz", "Nele", "Oskar",
        "Pia", "Rafael", "Sofia", "Theo", "Ute", "Vincent", "Hanna", "Leon", "Mila", "Erik", "Frieda", "Aylin",
    ];

    private static readonly string[] LastNames =
    [
        "Albers", "Brandt", "Conrad", "Dietrich", "Engel", "Fischer", "Graf", "Hoffmann", "Jansen", "Keller", "Lorenz", "Meyer",
        "Neumann", "Otto", "Peters", "Richter", "Schulz", "Thiele", "Ulrich", "Vogel", "Weber", "Yilmaz", "Zimmermann", "Arnold",
        "Böhm", "Claassen", "Dorn", "Ebert", "Franke", "Günther", "Haas", "Iversen", "Jung", "Kraus", "Lange", "Marx", "Nowak",
        "Pohl", "Roth", "Seidel",
    ];

    private static readonly string[] Departments = ["IT", "Vertrieb", "Buchhaltung", "Produktion", "Personal", "Einkauf", "Geschäftsführung"];

    public static IReadOnlyList<DemoGraph> BuildTenants()
    {
        KeySpecs.Clear();
        var contoso = Build(ContosoTenantId, "Contoso Demo GmbH", "contoso-demo.example", 40, 0.62, seed: 4711, serialBase: 31_408_000);
        // Contoso: Richtlinie lässt nur bestimmte Keys zu, die neuen 5.7-Keys fehlen → Prüfung zeigt „Jetzt beheben“.
        contoso.Policy = new Fido2Policy
        {
            Enabled = true,
            IsAttestationEnforced = true,
            KeyRestrictionsEnforced = true,
            EnforcementType = "allow",
            AaGuids = [Aaguid54, Aaguid54Nfc, Aaguid5Ci],
            IncludeTargets = [new PolicyTarget(PolicyTarget.AllUsers)],
        };
        var fabrikam = Build(FabrikamTenantId, "Fabrikam Demo AG", "fabrikam-demo.example", 22, 0.45, seed: 815, serialBase: 29_117_000);
        return [contoso, fabrikam];
    }

    /// <summary>Für Tenants, die jemand in der Demo neu anlegt: ein paar Personen, noch ohne Keys.</summary>
    public static DemoGraph BuildSmallTenant(Guid id) =>
        Build(id, "Neuer Demo-Tenant " + id.ToString()[..4], "tenant-" + id.ToString()[..8] + ".example", 8, 0, seed: id.GetHashCode(), serialBase: 0);

    private static DemoGraph Build(Guid tenantId, string name, string domain, int people, double keyShare, int seed, int serialBase)
    {
        var rnd = new Random(seed);
        var g = new DemoGraph(tenantId, name, domain);
        g.GroupNames[GroupYubiKey] = "YubiKey-Nutzer";
        g.GroupNames[GroupProduction] = "Produktion";
        g.GroupNames[GroupBreakglass] = "Notfallkonten (Breakglass)";
        foreach (var id in g.GroupNames.Keys) g.Groups[id] = [];
        var now = DateTimeOffset.UtcNow;
        var serial = serialBase;
        var used = new HashSet<string>();

        for (var i = 0; i < people; i++)
        {
            string first, last;
            do { first = FirstNames[rnd.Next(FirstNames.Length)]; last = LastNames[rnd.Next(LastNames.Length)]; } while (!used.Add(first + last));
            var upn = $"{Ascii(first)}.{Ascii(last)}@{domain}".ToLowerInvariant();
            var dept = i < 3 ? "IT" : i == 3 ? "Geschäftsführung" : Departments[rnd.Next(Departments.Length)];
            var id = NewId(rnd);
            var user = new GraphUser(id, upn, $"{first} {last}", upn, dept, AccountEnabled: i != people - 1 || people < 10);
            g.Users.Add(user);
            if (i < 2) g.Admins.Add(id);
            g.Groups[GroupYubiKey].Add(id);
            if (dept == "Produktion") g.Groups[GroupProduction].Add(id);
            g.LastSignIn[id] = now.AddHours(-rnd.Next(1, 24 * 40));

            // Weitere Methoden für die Rollout-Übersicht.
            var other = new List<GraphAuthMethod>();
            if (rnd.NextDouble() < 0.85) other.Add(new GraphAuthMethod("auth-" + i, "microsoftAuthenticator", "microsoftAuthenticatorPush", "iPhone von " + first, "iPhone", now.AddDays(-rnd.Next(200, 900))));
            if (rnd.NextDouble() < 0.35) other.Add(new GraphAuthMethod("phone-" + i, "phone", "mobilePhone", null, "+49 151 0000" + rnd.Next(1000, 9999), now.AddDays(-rnd.Next(300, 1200))));
            if (rnd.NextDouble() < 0.30) other.Add(new GraphAuthMethod("whfb-" + i, "windowsHelloForBusiness", "windowsHelloForBusiness", "NB-" + rnd.Next(1000, 9999), null, now.AddDays(-rnd.Next(30, 500))));
            if (rnd.NextDouble() < 0.12) other.Add(new GraphAuthMethod("pk-auth-" + i, "fido2", "passKeyDeviceBoundAuthenticator", "Passkey im Authenticator", "Microsoft Authenticator", now.AddDays(-rnd.Next(10, 120))));
            if (other.Count > 0) g.Other[id] = other;

            if (i >= 2 && rnd.NextDouble() < keyShare)
            {
                var (fw, model, form, aaguid) = RandomKey(rnd);
                var methodId = "demo-m-" + NewId(rnd)[..8];
                serial += rnd.Next(3, 40);
                g.Fido2[id] = [new GraphFido2Method(methodId, $"YubiKey {serial}", now.AddDays(-rnd.Next(5, 420)), aaguid, AaguidModel(aaguid))];
                // Gut jeder zehnte Key wurde selbst registriert (ohne Plattform): bleibt ohne Seriennummer, bis er nacherfasst wird.
                if (rnd.NextDouble() > 0.1) KeySpecs[methodId] = new DemoKeySpec(serial, fw, model, form);
            }
        }

        if (serialBase > 0)
        {
            // Zwei Notfallkonten, gemeinsam auf einem Breakglass-Key beim Verwahrer.
            var breakglassSerial = serial += 101;
            var custodian = g.Users[0].DisplayName + " (Tresor IT)";
            for (var b = 1; b <= 2; b++)
            {
                var id = NewId(rnd);
                var upn = $"breakglass0{b}@{domain}";
                g.Users.Add(new GraphUser(id, upn, $"Notfallkonto {b}", null, "IT", true));
                g.Admins.Add(id);
                g.Groups[GroupBreakglass].Add(id);
                g.Groups[GroupYubiKey].Remove(id);
                var methodId = "demo-bg-" + NewId(rnd)[..8];
                g.Fido2[id] = [new GraphFido2Method(methodId, $"YubiKey {breakglassSerial}", now.AddDays(-180), Aaguid57Nfc, AaguidModel(Aaguid57Nfc))];
                KeySpecs[methodId] = new DemoKeySpec(breakglassSerial, "5.7.1", "YubiKey 5 NFC", "UsbAKeychain", KeyUsage.Breakglass, custodian);
            }

            // Geteilter Key in der Produktion: mehrere Konten auf einem Key, verwahrt von der Schichtleitung.
            var sharedSerial = serial += 57;
            foreach (var u in g.Users.Where(u => g.Groups[GroupProduction].Contains(u.Id) && !g.Fido2.ContainsKey(u.Id)).Take(3))
            {
                var methodId = "demo-sh-" + NewId(rnd)[..8];
                g.Fido2[u.Id] = [new GraphFido2Method(methodId, $"YubiKey {sharedSerial}", now.AddDays(-75), Aaguid57Nfc, AaguidModel(Aaguid57Nfc))];
                KeySpecs[methodId] = new DemoKeySpec(sharedSerial, "5.7.1", "YubiKey 5 NFC", "UsbAKeychain", KeyUsage.Shared, "Schichtleitung Produktion");
            }
        }
        return g;
    }

    private static (string Firmware, string Model, string FormFactor, Guid Aaguid) RandomKey(Random rnd)
    {
        var r = rnd.NextDouble();
        return r switch
        {
            < 0.40 => ("5.7.1", "YubiKey 5 NFC", "UsbAKeychain", Aaguid57Nfc),
            < 0.55 => ("5.7.4", "YubiKey 5C NFC", "UsbCKeychain", Aaguid57Nfc),
            < 0.62 => ("5.7.1", "YubiKey 5C Nano", "UsbCNano", Aaguid57),
            < 0.85 => ("5.4.3", "YubiKey 5 NFC", "UsbAKeychain", Aaguid54Nfc),
            < 0.92 => ("5.4.3", "YubiKey 5Ci", "UsbCLightning", Aaguid5Ci),
            _ => ("5.2.7", "YubiKey 5C", "UsbCKeychain", Aaguid54),
        };
    }

    public static string AaguidModel(Guid aaguid) => aaguid == Aaguid57Nfc ? "YubiKey 5 Series with NFC"
        : aaguid == Aaguid57 ? "YubiKey 5 Series"
        : aaguid == Aaguid54Nfc ? "YubiKey 5 Series with NFC"
        : aaguid == Aaguid5Ci ? "YubiKey 5Ci"
        : "YubiKey 5 Series";

    /// <summary>Feste IDs je Startwert: Nach einem Neustart passt der Tenant im Speicher wieder zu den Daten in der Datenbank.</summary>
    private static string NewId(Random rnd)
    {
        var bytes = new byte[16];
        rnd.NextBytes(bytes);
        return new Guid(bytes).ToString();
    }

    private static string Ascii(string s) => s.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("Ä", "Ae").Replace("Ö", "Oe").Replace("Ü", "Ue").Replace("ß", "ss");

    public static readonly Guid AuthenticatorPasskeyAaguid = AuthenticatorAaguid;
}
