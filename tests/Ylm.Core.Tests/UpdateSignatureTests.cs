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
}
