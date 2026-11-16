namespace Ylm.Core;

/// <summary>
/// AAGUIDs of common YubiKey models. Used by the tenant check "YubiKey-AAGUIDs erlaubt".
/// Source: Yubico "YubiKey Hardware FIDO2 AAGUIDs" (docs.yubico.com). The list can be extended
/// per installation (configuration key Ylm:AdditionalYubiKeyAaguids); AAGUIDs read from real
/// keys during registration are checked as well.
/// </summary>
public static class YubicoAaguids
{
    public static readonly IReadOnlyDictionary<Guid, string> Known = new Dictionary<Guid, string>
    {
        [Guid.Parse("cb69481e-8ff7-4039-93ec-0a2729a154a8")] = "YubiKey 5 Series (FW 5.1)",
        [Guid.Parse("ee882879-721c-4913-9775-3dfcce97072a")] = "YubiKey 5 Series (FW 5.2/5.4)",
        [Guid.Parse("fa2b99dc-9e39-4257-8f92-4a30d23c4118")] = "YubiKey 5 Series with NFC (FW 5.1)",
        [Guid.Parse("2fc0579f-8113-47ea-b116-bb5a8db9202a")] = "YubiKey 5 Series with NFC (FW 5.2/5.4)",
        [Guid.Parse("c5ef55ff-ad9a-4b9f-b580-adebafe026d0")] = "YubiKey 5Ci (FW 5.2/5.4)",
        [Guid.Parse("19083c3d-8383-4b18-bc03-8f1c9ab2fd1b")] = "YubiKey 5 Series (FW 5.7)",
        [Guid.Parse("a25342c0-3cdc-4414-8e46-f4807fca511c")] = "YubiKey 5 Series with NFC (FW 5.7)",
        [Guid.Parse("73bb0cd4-e502-49b8-9c6f-b59445bf720b")] = "YubiKey 5 FIPS Series (FW 5.4)",
        [Guid.Parse("c1f9a0bc-1dd2-404a-b27f-8e29047a43fd")] = "YubiKey 5 FIPS Series with NFC (FW 5.4)",
        [Guid.Parse("85203421-48f9-4355-9bc8-8a53846e5083")] = "YubiKey 5 FIPS Series with Lightning (FW 5.4)",
        [Guid.Parse("d8522d9f-575b-4866-88a9-ba99fa02f35b")] = "YubiKey Bio Series (FW 5.5/5.6)",
        [Guid.Parse("149a2021-8ef6-4133-96b8-81f8d5b7f1f5")] = "Security Key by Yubico with NFC (FW 5.1)",
        [Guid.Parse("a4e9fc6d-4cbe-4758-b8ba-37598bb5bbaa")] = "Security Key by Yubico with NFC (FW 5.2/5.4)",
        [Guid.Parse("b92c3f9a-c014-4056-887f-140a2501163b")] = "Security Key by Yubico (FW 5.1)",
        [Guid.Parse("6d44ba9b-f6ec-2e49-b930-0c8fe920cb73")] = "Security Key by Yubico with NFC (Enterprise)",
    };

    public static bool IsYubiKey(Guid? aaGuid, string? model) =>
        (aaGuid is { } g && Known.ContainsKey(g))
        || (model?.Contains("YubiKey", StringComparison.OrdinalIgnoreCase) ?? false)
        || (model?.Contains("Yubico", StringComparison.OrdinalIgnoreCase) ?? false);
}
