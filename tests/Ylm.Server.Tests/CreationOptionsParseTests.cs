using System.Text;
using Ylm.Agent.Core;

namespace Ylm.Server.Tests;

public sealed class CreationOptionsParseTests
{
    [Fact]
    public void Base64url_fields_are_decoded()
    {
        var json = """{"challenge":"AQID_-8","rp":{"id":"login.microsoft.com","name":"Microsoft"},"user":{"id":"BAUG","name":"a@b.c","displayName":"A"},"pubKeyCredParams":[{"type":"public-key","alg":-7}]}""";
        var o = WebAuthnCreationOptions.Parse(json);
        Assert.Equal(new byte[] { 1, 2, 3, 0xFF, 0xEF }, o.Challenge);
        Assert.Equal(new byte[] { 4, 5, 6 }, o.User.Id);
    }

    [Fact]
    public void Non_base64_challenge_is_used_as_text()
    {
        const string challenge = "eyJhbGciOiJSUzI1NiJ9.eyJ4IjoxfQ.c2ln";
        var json = $$"""{"challenge":"{{challenge}}","rp":{"id":"login.microsoft.com"},"user":{"id":"BAUG","name":"a","displayName":"A"}}""";
        Base64Url.TextFallbacks = [];
        var o = WebAuthnCreationOptions.Parse(json);
        Assert.Equal(Encoding.UTF8.GetBytes(challenge), o.Challenge);
        Assert.Single(Base64Url.TextFallbacks);
        Base64Url.TextFallbacks = null;
    }

    [Fact]
    public void Padded_standard_base64_is_accepted()
    {
        Assert.True(Base64Url.TryDecode("AQID/w==", out var bytes));
        Assert.Equal(new byte[] { 1, 2, 3, 0xFF }, bytes);
        Assert.False(Base64Url.TryDecode("abcde", out _));
    }
}
