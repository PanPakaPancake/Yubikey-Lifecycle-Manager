using System.Formats.Cbor;
using System.Text;
using System.Text.Json;
using Ylm.Agent.Core;
using Ylm.Contracts;
using Ylm.Core;

namespace Ylm.Core.Tests;

public class RoleTests
{
    [Fact]
    public void Owner_has_everything() => Assert.True(RolePermissions.Has(PlatformRole.Owner, Permission.ManageTenants | Permission.RegisterKeys | Permission.ManageUsers));

    [Fact]
    public void Helpdesk_can_lock_but_not_register()
    {
        Assert.True(RolePermissions.Has(PlatformRole.Helpdesk, Permission.LockKeys));
        Assert.True(RolePermissions.Has(PlatformRole.Helpdesk, Permission.ReadInventory));
        Assert.False(RolePermissions.Has(PlatformRole.Helpdesk, Permission.RegisterKeys));
        Assert.False(RolePermissions.Has(PlatformRole.Helpdesk, Permission.ResetKeys));
    }

    [Fact]
    public void Auditor_is_read_only()
    {
        Assert.True(RolePermissions.Has(PlatformRole.Auditor, Permission.ReadAudit | Permission.ReadReports | Permission.ReadInventory));
        Assert.False(RolePermissions.Has(PlatformRole.Auditor, Permission.LockKeys));
    }

    [Fact]
    public void Operator_registers_but_does_not_manage_tenants()
    {
        Assert.True(RolePermissions.Has(PlatformRole.Operator, Permission.RegisterKeys | Permission.ResetKeys | Permission.LockKeys));
        Assert.False(RolePermissions.Has(PlatformRole.Operator, Permission.ManageTenants));
        Assert.False(RolePermissions.Has(PlatformRole.Operator, Permission.ManageProfiles));
    }
}

public class LifecycleTests
{
    [Theory]
    [InlineData(KeyStatus.InStock, KeyStatus.Active, true)]
    [InlineData(KeyStatus.Active, KeyStatus.Locked, true)]
    [InlineData(KeyStatus.Locked, KeyStatus.Retired, true)]
    [InlineData(KeyStatus.Locked, KeyStatus.InStock, true)]
    [InlineData(KeyStatus.Active, KeyStatus.Retired, false)]
    [InlineData(KeyStatus.Retired, KeyStatus.InStock, false)]
    [InlineData(KeyStatus.Locked, KeyStatus.Active, false)]
    public void Transitions(KeyStatus from, KeyStatus to, bool allowed) => Assert.Equal(allowed, KeyLifecycle.CanTransition(from, to));

    [Fact]
    public void Invalid_transition_throws_with_german_message()
    {
        var ex = Assert.Throws<DomainException>(() => KeyLifecycle.EnsureTransition(KeyStatus.Retired, KeyStatus.Active));
        Assert.Contains("Ausgemustert", ex.Message);
    }
}

public class FirmwareTests
{
    [Theory]
    [InlineData("5.7.1", "5.7.0", 1)]
    [InlineData("5.4.3", "5.7.0", -1)]
    [InlineData("5.7", "5.7.0", 0)]
    public void Compare(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(FirmwareVersion.Parse(a).CompareTo(FirmwareVersion.Parse(b))));

    [Fact]
    public void Eucleak_applies_below_57()
    {
        Assert.Contains(FirmwareAdvisories.For("5.4.3"), a => a.Id == "YSA-2024-03");
        Assert.DoesNotContain(FirmwareAdvisories.For("5.7.0"), a => a.Id == "YSA-2024-03");
        Assert.Empty(FirmwareAdvisories.For(null));
    }
}

public class ProfileEvaluatorTests
{
    private static KeyInfo Key(string fw = "5.7.1", bool force = true, bool pinSet = false, string model = "YubiKey 5 NFC") => new()
    {
        SerialNumber = 123, FirmwareVersion = fw, Model = model, SupportsForcePinChange = force, PinIsSet = pinSet, MinimumPinLength = 4,
    };

    [Fact]
    public void Standard_profile_with_57_key_is_compatible() => Assert.True(ProfileEvaluator.Evaluate(new ProfileSettings(), Key()).Compatible);

    [Fact]
    public void Old_firmware_cannot_force_pin_change_but_can_be_overridden()
    {
        var r = ProfileEvaluator.Evaluate(new ProfileSettings(), Key("5.4.3", force: false));
        var v = Assert.Single(r.Violations);
        Assert.Equal(ViolationCode.ForcePinChangeUnsupported, v.Code);
        Assert.True(v.CanOverride);
        Assert.NotEmpty(r.Blocking([], false));
        Assert.Empty(r.Blocking([ViolationCode.ForcePinChangeUnsupported], false));
    }

    [Fact]
    public void Pin_already_set_requires_reset_and_cannot_be_overridden()
    {
        var r = ProfileEvaluator.Evaluate(new ProfileSettings(), Key(pinSet: true));
        Assert.Contains(r.Violations, v => v.Code == ViolationCode.PinAlreadySet && !v.CanOverride && v.ResolvedByReset);
        Assert.NotEmpty(r.Blocking([ViolationCode.PinAlreadySet], false));
        Assert.Empty(r.Blocking([], resetPlanned: true));
    }

    [Fact]
    public void Model_and_minimum_firmware()
    {
        var p = new ProfileSettings { AllowedModels = ["YubiKey 5"], MinimumFirmware = "5.7" };
        Assert.True(ProfileEvaluator.Evaluate(p, Key()).Compatible);
        var r = ProfileEvaluator.Evaluate(p, Key("5.2.7", force: false, model: "Security Key NFC"));
        Assert.Contains(r.Violations, v => v.Code == ViolationCode.ModelNotAllowed);
        Assert.Contains(r.Violations, v => v.Code == ViolationCode.FirmwareBelowMinimum);
    }

    [Fact]
    public void No_fido_is_hard_stop()
    {
        var r = ProfileEvaluator.Evaluate(new ProfileSettings(), Key() with { HasFido2 = false });
        Assert.False(Assert.Single(r.Violations).CanOverride);
    }
}

public class PinTests
{
    [Fact]
    public void Generated_pins_have_length_and_are_not_trivial()
    {
        for (var i = 0; i < 500; i++)
        {
            var pin = PinGenerator.Generate(8);
            Assert.Equal(8, pin.Length);
            Assert.All(pin, c => Assert.True(char.IsAsciiDigit(c)));
            Assert.False(PinGenerator.IsTrivial(pin));
        }
    }

    [Theory]
    [InlineData("11111111")]
    [InlineData("12345678")]
    [InlineData("87654321")]
    [InlineData("12121212")]
    [InlineData("123123")]
    public void Trivial_pins_are_detected(string pin) => Assert.True(PinGenerator.IsTrivial(pin));

    [Fact]
    public void Fixed_pin_validation()
    {
        Assert.NotNull(PinGenerator.ValidateFixedPin("123", 4));
        Assert.NotNull(PinGenerator.ValidateFixedPin("123456", 6));
        Assert.Null(PinGenerator.ValidateFixedPin("739184", 6));
    }

    [Fact]
    public void Display_name_template()
    {
        var key = new KeyInfo { SerialNumber = 4711, FirmwareVersion = "5.7.1", Model = "YubiKey 5C NFC" };
        Assert.Equal("YubiKey 4711", DisplayNameTemplate.Render("YubiKey {Seriennummer}", key, "a@b.de", new DateOnly(2026, 11, 15)));
        Assert.Equal("YubiKey 5C NFC a@b.de 2026-11-15", DisplayNameTemplate.Render("{Modell} {UPN} {Datum}", key, "a@b.de", new DateOnly(2026, 11, 15)));
    }

    [Theory]
    [InlineData("UsbAKeychain", true, false, false, 5, "YubiKey 5 NFC")]
    [InlineData("UsbCKeychain", true, false, false, 5, "YubiKey 5C NFC")]
    [InlineData("UsbCNano", false, false, false, 5, "YubiKey 5C Nano")]
    [InlineData("UsbCLightning", false, false, false, 5, "YubiKey 5Ci")]
    [InlineData("UsbAKeychain", true, true, false, 5, "Security Key NFC")]
    [InlineData("UsbAKeychain", true, false, true, 5, "YubiKey 5 NFC FIPS")]
    public void Model_names(string ff, bool nfc, bool sky, bool fips, int major, string expected) =>
        Assert.Equal(expected, KeyModelNames.Describe(ff, nfc, sky, fips, major));
}

public class TenantCheckTests
{
    private static readonly Guid Yk = YubicoAaguids.Known.Keys.First();
    private static readonly Guid[] All = YubicoAaguids.Known.Keys.ToArray();

    [Fact]
    public void Disabled_policy_with_allow_list_fails_and_fix_repairs_everything()
    {
        var policy = new Fido2Policy { Enabled = false, KeyRestrictionsEnforced = true, EnforcementType = "allow", AaGuids = [Yk], IncludeTargets = [] };
        var results = Fido2PolicyAnalyzer.Analyze(policy, All, null);
        Assert.All(results, r => Assert.Equal(CheckStatus.Failed, r.Status));
        Assert.All(results, r => Assert.True(r.CanFix));
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.ManualInstructions)));

        var fixedPolicy = Fido2PolicyAnalyzer.Fix(policy, All, null, results.Select(r => r.Id).ToList());
        Assert.All(Fido2PolicyAnalyzer.Analyze(fixedPolicy, All, null), r => Assert.Equal(CheckStatus.Ok, r.Status));
        Assert.Equal(All.Length, fixedPolicy.AaGuids.Count);
    }

    [Fact]
    public void Block_list_fix_removes_yubikeys()
    {
        var other = Guid.NewGuid();
        var policy = new Fido2Policy { Enabled = true, KeyRestrictionsEnforced = true, EnforcementType = "block", AaGuids = [Yk, other], IncludeTargets = [new(PolicyTarget.AllUsers)] };
        var fixedPolicy = Fido2PolicyAnalyzer.Fix(policy, All, null, [TenantCheckIds.AaguidsAllowed]);
        Assert.Equal([other], fixedPolicy.AaGuids);
    }

    [Fact]
    public void Target_group_is_added_and_removed_from_excludes()
    {
        var policy = new Fido2Policy { Enabled = true, IncludeTargets = [new("g1")], ExcludeTargets = [new("g2")] };
        var results = Fido2PolicyAnalyzer.Analyze(policy, All, [new GroupRef("g2", "G2")]);
        Assert.Equal(CheckStatus.Failed, results.Single(r => r.Id == TenantCheckIds.TargetGroup).Status);
        var fixedPolicy = Fido2PolicyAnalyzer.Fix(policy, All, [new GroupRef("g2", "G2")], [TenantCheckIds.TargetGroup]);
        Assert.Contains(fixedPolicy.IncludeTargets, t => t.Id == "g2");
        Assert.Empty(fixedPolicy.ExcludeTargets);
    }

    [Fact]
    public void Multiple_target_groups_are_all_checked_and_fixed()
    {
        GroupRef[] groups = [new("g1", "Vertrieb"), new("g2", "Produktion"), new("g3", "IT")];
        var policy = new Fido2Policy { Enabled = true, IncludeTargets = [new("g1")], ExcludeTargets = [new("g3"), new("other")] };
        var check = Fido2PolicyAnalyzer.Analyze(policy, All, groups).Single(r => r.Id == TenantCheckIds.TargetGroup);
        Assert.Equal(CheckStatus.Failed, check.Status);
        Assert.Contains("IT", check.Message);

        var fixedPolicy = Fido2PolicyAnalyzer.Fix(policy, All, groups, [TenantCheckIds.TargetGroup]);
        Assert.Equal(["g1", "g2", "g3"], fixedPolicy.IncludeTargets.Select(t => t.Id).Order());
        Assert.Equal(["other"], fixedPolicy.ExcludeTargets.Select(t => t.Id));
        Assert.Equal(CheckStatus.Ok, Fido2PolicyAnalyzer.Analyze(fixedPolicy, All, groups).Single(r => r.Id == TenantCheckIds.TargetGroup).Status);
    }

    [Fact]
    public void All_users_in_policy_covers_every_target_group()
    {
        var policy = new Fido2Policy { Enabled = true, IncludeTargets = [new(PolicyTarget.AllUsers)] };
        var check = Fido2PolicyAnalyzer.Analyze(policy, All, [new GroupRef("g1", "A"), new GroupRef("g2", "B")]).Single(r => r.Id == TenantCheckIds.TargetGroup);
        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Single(Fido2PolicyAnalyzer.Fix(policy, All, [new GroupRef("g1", "A")], [TenantCheckIds.TargetGroup]).IncludeTargets);
    }

    [Fact]
    public void Only_selected_checks_are_fixed()
    {
        var policy = new Fido2Policy { Enabled = false, IncludeTargets = [] };
        var fixedPolicy = Fido2PolicyAnalyzer.Fix(policy, All, null, [TenantCheckIds.Fido2Enabled]);
        Assert.True(fixedPolicy.Enabled);
        Assert.Empty(fixedPolicy.IncludeTargets);
    }
}

public class CsvAndFieldTests
{
    [Fact]
    public void Csv_escapes_and_blocks_formulas()
    {
        Assert.Equal("'=SUM(A1)", Csv.Escape("=SUM(A1)"));
        Assert.Equal("\"a;b\"", Csv.Escape("a;b"));
        Assert.Equal("\"x\"\"y\"", Csv.Escape("x\"y"));
        var bytes = Csv.Write(["A", "B"], [["1", "2"]]);
        Assert.Equal(0xEF, bytes[0]);
    }

    [Fact]
    public void Csv_reader_handles_quotes_and_separators()
    {
        var rows = Csv.Read("﻿Seriennummer;Tags\r\n123;\"a, b\"\r\n456;\"x;\"\"y\"\"\"\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal("a, b", rows[1][1]);
        Assert.Equal("x;\"y\"", rows[2][1]);
        Assert.Equal(2, Csv.Read("serial,model\n1,a\n").Count);
    }

    [Fact]
    public void Custom_fields_are_validated()
    {
        CustomFieldSpec[] specs =
        [
            new("standort", "Standort", CustomFieldType.Select, true, ["Berlin", "Köln"]),
            new("kosten", "Kostenstelle", CustomFieldType.Number, false, []),
            new("datum", "Bestelldatum", CustomFieldType.Date, false, []),
        ];
        Assert.Empty(CustomFieldValidator.Validate(specs, new Dictionary<string, string?> { ["standort"] = "Berlin", ["kosten"] = "4711", ["datum"] = "2026-01-31" }));
        var errors = CustomFieldValidator.Validate(specs, new Dictionary<string, string?> { ["kosten"] = "abc", ["datum"] = "31.01.2026", ["foo"] = "x" });
        Assert.Equal(4, errors.Count);
    }

    [Fact]
    public void Tags_are_normalized() => Assert.Equal(["Berlin", "neu"], Tags.Normalize([" neu", "Berlin", "NEU", ""]));
}

public class AuditChainTests
{
    [Fact]
    public void Hash_depends_on_every_field_and_survives_microsecond_truncation()
    {
        var t = DateTimeOffset.UtcNow;
        var a = AuditChain.ComputeHash(AuditChain.Genesis, 1, t, "x", "key.locked", null, null, "1", "{}");
        Assert.Equal(a, AuditChain.ComputeHash(AuditChain.Genesis, 1, AuditChain.Normalize(t), "x", "key.locked", null, null, "1", "{}"));
        Assert.NotEqual(a, AuditChain.ComputeHash(AuditChain.Genesis, 1, t, "x", "key.retired", null, null, "1", "{}"));
        Assert.NotEqual(a, AuditChain.ComputeHash(a, 1, t, "x", "key.locked", null, null, "1", "{}"));
    }
}

public class WebAuthnTests
{
    private const string GraphOptions = """
        {
          "challengeTimeoutDateTime": "2026-11-15T12:05:00Z",
          "publicKey": {
            "challenge": "dGVzdC1jaGFsbGVuZ2UtMTIzNDU2Nzg5MA",
            "timeout": 300000,
            "attestation": "direct",
            "rp": { "id": "login.microsoft.com", "name": "Microsoft" },
            "user": { "id": "T0JKRUNULUlE", "name": "anna@contoso.com", "displayName": "Anna Beispiel" },
            "pubKeyCredParams": [ { "type": "public-key", "alg": -7 }, { "type": "public-key", "alg": -257 } ],
            "excludeCredentials": [ { "id": "AQIDBA", "type": "public-key", "transports": ["usb"] } ],
            "authenticatorSelection": { "authenticatorAttachment": "cross-platform", "requireResidentKey": true, "userVerification": "required" },
            "extensions": { "hmacCreateSecret": true, "enforceCredentialProtectionPolicy": true, "credentialProtectionPolicy": "userVerificationOptional" }
          }
        }
        """;

    [Fact]
    public void Parses_graph_creation_options()
    {
        var o = WebAuthnCreationOptions.Parse(GraphOptions);
        Assert.Equal("login.microsoft.com", o.RpId);
        Assert.Equal("test-challenge-1234567890", Encoding.UTF8.GetString(o.Challenge));
        Assert.Equal("OBJECT-ID", Encoding.UTF8.GetString(o.User.Id));
        Assert.Equal([-7, -257], o.Algorithms);
        Assert.Equal([1, 2, 3, 4], Assert.Single(o.ExcludeCredentials).Id);
        Assert.True(o.ResidentKey);
        Assert.True(o.HmacSecret);
        Assert.Equal(1, o.CredProtect);
        Assert.True(o.EnforceCredProtect);
    }

    [Fact]
    public void Client_data_json_matches_webauthn()
    {
        var json = WebAuthnEncoding.ClientDataJson([1, 2, 3], "https://login.microsoft.com");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("webauthn.create", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("AQID", doc.RootElement.GetProperty("challenge").GetString());
        Assert.Equal("https://login.microsoft.com", doc.RootElement.GetProperty("origin").GetString());
    }

    [Fact]
    public async Task Simulated_device_produces_decodable_attestation_object()
    {
        var device = new SimulatedKeyDevice();
        Assert.True((await device.SetPinAsync(null, "73918462")).Success);
        var options = WebAuthnCreationOptions.Parse(GraphOptions);
        var result = await device.MakeCredentialAsync(null, "73918462", options, "https://login.microsoft.com", new NoPrompts());
        Assert.True(result.Success, result.Error);

        var reader = new CborReader(Base64Url.Decode(result.AttestationObject!), CborConformanceMode.Ctap2Canonical);
        Assert.Equal(3, reader.ReadStartMap());
        Assert.Equal("fmt", reader.ReadTextString());
        Assert.Equal("none", reader.ReadTextString());
        Assert.Equal("attStmt", reader.ReadTextString());
        reader.SkipValue();
        Assert.Equal("authData", reader.ReadTextString());
        var authData = reader.ReadByteString();
        Assert.Equal(SimulatedKeyDevice.SimulatedAaguid, WebAuthnEncoding.AaguidFromAuthData(authData));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData("login.microsoft.com"u8.ToArray()), authData[..32]);

        Assert.True((await device.ForcePinChangeAsync(null, "73918462")).Success);
        Assert.True(device.ForcePinChangeActive);
        Assert.False((await device.SetPinAsync(null, "11112222")).Success);
    }

    private sealed class NoPrompts : IOperationPrompts { public void Prompt(string text) { } }
}

public class CustomFieldInheritanceTests
{
    private static readonly Guid G1 = Guid.NewGuid(), G2 = Guid.NewGuid(), G3 = Guid.NewGuid(), L1 = Guid.NewGuid();

    private static CustomFieldDef Def(Guid id, string key, CustomFieldType type = CustomFieldType.Text, bool required = false, params string[] options) =>
        new(id, new CustomFieldSpec(key, key.ToUpperInvariant(), type, required, options), 0);

    [Fact]
    public void Global_fields_are_inherited_modified_or_disabled_and_local_fields_follow()
    {
        var global = new[] { Def(G1, "kostenstelle"), Def(G2, "standort", CustomFieldType.Select, false, "Berlin", "Köln"), Def(G3, "inventar") };
        var overrides = new Dictionary<Guid, CustomFieldOverrideSpec>
        {
            [G2] = new(false, "Werk", true, ["Hamburg"]),
            [G3] = new(true, null, null, null),
        };
        var resolved = CustomFieldInheritance.Resolve(global, overrides, [Def(L1, "raum")]);

        Assert.Equal(["kostenstelle", "standort", "inventar", "raum"], resolved.Select(r => r.Spec.Key));
        Assert.Equal([CustomFieldState.Inherited, CustomFieldState.Modified, CustomFieldState.Disabled, CustomFieldState.Local], resolved.Select(r => r.State));
        Assert.Equal([CustomFieldOrigin.Global, CustomFieldOrigin.Global, CustomFieldOrigin.Global, CustomFieldOrigin.Tenant], resolved.Select(r => r.Origin));

        var standort = resolved[1];
        Assert.Equal("Werk", standort.Spec.Label);
        Assert.True(standort.Spec.Required);
        Assert.Equal(["Hamburg"], standort.Spec.Options);
        Assert.Equal("STANDORT", standort.Inherited!.Label);
        Assert.False(resolved[2].IsActive);
    }

    [Fact]
    public void Override_without_changes_counts_as_inherited()
    {
        var g = Def(G1, "kostenstelle");
        var noOp = new CustomFieldOverrideSpec(false, "KOSTENSTELLE", false, null);
        Assert.True(CustomFieldInheritance.IsNoOp(g.Spec, noOp));
        Assert.Equal(CustomFieldState.Inherited, CustomFieldInheritance.Resolve([g], new Dictionary<Guid, CustomFieldOverrideSpec> { [G1] = noOp }, []).Single().State);
    }

    [Fact]
    public void Options_override_is_ignored_for_non_select_fields()
    {
        var g = Def(G1, "nummer", CustomFieldType.Number);
        Assert.Empty(CustomFieldInheritance.Apply(g.Spec, new(false, null, null, ["x"])).Options);
    }
}

public sealed class AuthMethodCatalogTests
{
    [Theory]
    [InlineData(new[] { "password", "passKeyDeviceBound", "mobilePhone" }, RolloutStage.SecurityKey)]
    [InlineData(new[] { "password", "fido2" }, RolloutStage.SecurityKey)]
    [InlineData(new[] { "password", "passKeyDeviceBoundAuthenticator" }, RolloutStage.OtherPasskey)]
    [InlineData(new[] { "windowsHelloForBusiness", "microsoftAuthenticatorPush" }, RolloutStage.OtherPasskey)]
    [InlineData(new[] { "password", "microsoftAuthenticatorPush" }, RolloutStage.ClassicMfa)]
    [InlineData(new[] { "password", "email", "temporaryAccessPass" }, RolloutStage.NoMfa)]
    [InlineData(new string[0], RolloutStage.NoMfa)]
    public void Stage_follows_strongest_method(string[] methods, RolloutStage expected) =>
        Assert.Equal(expected, AuthMethodCatalog.StageOf(methods));

    [Fact]
    public void Phone_types_map_to_report_names()
    {
        Assert.Equal("mobilePhone", AuthMethodCatalog.ReportNameFor("phone", "mobile"));
        Assert.Equal("officePhone", AuthMethodCatalog.ReportNameFor("phone", "office"));
        Assert.False(AuthMethodCatalog.IsRemovable("password"));
        Assert.False(AuthMethodCatalog.IsRemovable("fido2"));
        Assert.False(AuthMethodCatalog.IsRemovable("fido2", "passKeyDeviceBound"));
        Assert.True(AuthMethodCatalog.IsRemovable("fido2", "passKeyDeviceBoundAuthenticator"));
        Assert.True(AuthMethodCatalog.IsRemovable("fido2", "passKeySynced"));
        Assert.True(AuthMethodCatalog.IsRemovable("phone"));
    }
}

public sealed class Fido2ClassificationTests
{
    [Theory]
    [InlineData("90a3ccdf-635c-4729-a248-9b709135078f", "Microsoft Authenticator - iOS", "passKeyDeviceBoundAuthenticator")]
    [InlineData(null, "Microsoft Authenticator - Android", "passKeyDeviceBoundAuthenticator")]
    [InlineData("08987058-cadc-4b81-b6e1-30de50dcbe96", null, "passKeyDeviceBoundWindowsHello")]
    [InlineData("00000000-0000-0000-0000-000000000000", null, "passKeySynced")]
    [InlineData("cb69481e-8ff7-4039-93ec-0a2729a154a8", "YubiKey 5 Series", "passKeyDeviceBound")]
    public void Fido2_is_classified_by_aaguid_and_model(string? aaguid, string? model, string expected)
    {
        Assert.Equal(expected, AuthMethodCatalog.Fido2ReportName(aaguid is null ? null : Guid.Parse(aaguid), model));
        Assert.Equal(expected == "passKeyDeviceBound", AuthMethodCatalog.Get(expected).IsSecurityKey);
    }
}
