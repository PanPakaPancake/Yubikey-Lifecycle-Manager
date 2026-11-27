using Ylm.Agent.Core;

namespace Ylm.Core.Tests;

public class UpdateSignatureTests
{
    private static readonly SignatureInfo Signed = new(true, "CN=Contoso GmbH, O=Contoso GmbH, C=DE");
    private static readonly SignatureInfo Other = new(true, "CN=Fremd AG, O=Fremd AG, C=DE");
    private static readonly SignatureInfo Unsigned = SignatureInfo.None;

    [Fact]
    public void Unsigned_agent_accepts_unsigned_update_for_migration() =>
        Assert.Null(UpdateSignaturePolicy.Check(Unsigned, Unsigned, false, null));

    [Fact]
    public void Unsigned_agent_accepts_signed_update() =>
        Assert.Null(UpdateSignaturePolicy.Check(Unsigned, Signed, false, null));

    [Fact]
    public void Signed_agent_rejects_unsigned_update() =>
        Assert.NotNull(UpdateSignaturePolicy.Check(Signed, Unsigned, false, null));

    [Fact]
    public void Signed_agent_rejects_other_signer() =>
        Assert.Contains("Fremd AG", UpdateSignaturePolicy.Check(Signed, Other, false, null));

    [Fact]
    public void Same_signer_matches_ignoring_spacing_and_case() =>
        Assert.Null(UpdateSignaturePolicy.Check(Signed, new(true, "cn=Contoso GmbH,O=Contoso GmbH,  C=DE"), false, null));

    [Fact]
    public void Required_signature_rejects_unsigned_update_even_for_unsigned_agent() =>
        Assert.NotNull(UpdateSignaturePolicy.Check(Unsigned, Unsigned, true, null));

    [Fact]
    public void Trusted_signer_allows_signer_change() =>
        Assert.Null(UpdateSignaturePolicy.Check(Signed, Other, false, "CN=Fremd AG, O=Fremd AG, C=DE"));

    [Fact]
    public void Trusted_signer_restricts_unsigned_agent() =>
        Assert.NotNull(UpdateSignaturePolicy.Check(Unsigned, Signed, false, "CN=Fremd AG, O=Fremd AG, C=DE"));

    [Fact]
    public void Invalid_signature_is_rejected() =>
        Assert.NotNull(UpdateSignaturePolicy.Check(Signed, new(false, Signed.Subject), false, null));

    [Theory]
    [InlineData("{6B0B4E2C-6F7A-4E0F-9C1C-3D1F4C7A2E51}")]
    [InlineData("6b0b4e2c-6f7a-4e0f-9c1c-3d1f4c7a2e51")]
    public void Agent_package_is_accepted(string code) =>
        Assert.Null(UpdateSignaturePolicy.CheckProduct(code));

    [Theory]
    [InlineData("{11111111-2222-3333-4444-555555555555}")]
    [InlineData("")]
    [InlineData(null)]
    public void Foreign_or_unreadable_package_is_rejected(string? code) =>
        Assert.NotNull(UpdateSignaturePolicy.CheckProduct(code));
}
