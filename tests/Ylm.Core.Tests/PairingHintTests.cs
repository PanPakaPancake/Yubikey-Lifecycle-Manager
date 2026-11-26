using System.Net.Sockets;
using System.Security.Authentication;
using Ylm.Agent.Core;

namespace Ylm.Core.Tests;

public sealed class PairingHintTests
{
    [Fact]
    public void Https_on_the_http_port_explains_the_address()
    {
        var ex = new HttpRequestException("The SSL connection could not be established.",
            new AuthenticationException("Cannot determine the frame size or a corrupted frame was received."));
        var hint = AgentConnection.ConnectionHint(ex, new Uri("https://localhost:8080"));
        Assert.NotNull(hint);
        Assert.Contains("Port 8080", hint);
        Assert.Contains("https://localhost", hint);
    }

    [Fact]
    public void Untrusted_certificate_points_to_the_root_certificate()
    {
        var ex = new HttpRequestException("The SSL connection could not be established.",
            new AuthenticationException("The remote certificate is invalid according to the validation procedure: RemoteCertificateChainErrors"));
        Assert.Contains("ylm-root-ca.crt", AgentConnection.ConnectionHint(ex, new Uri("https://ylm.firma.local"))!);
    }

    [Fact]
    public void Refused_connection_names_the_address()
    {
        var ex = new HttpRequestException("Verbindung verweigert", new SocketException((int)SocketError.ConnectionRefused));
        Assert.Contains("http://localhost:8080", AgentConnection.ConnectionHint(ex, new Uri("http://localhost:8080"))!);
    }

    [Fact]
    public void Other_errors_have_no_hint() =>
        Assert.Null(AgentConnection.ConnectionHint(new TaskCanceledException(), new Uri("https://localhost")));

    [Fact]
    public void Description_carries_type_and_code()
    {
        var text = AgentConnection.Describe(new HttpRequestException("x", new System.Security.Cryptography.CryptographicException("Unknown error (0x1770)") { HResult = unchecked((int)0x80071770) }));
        Assert.Contains("CryptographicException, 0x80071770", text);
    }

    private static HttpRequestException Schannel(uint status, string message) =>
        new("The SSL connection could not be established, see inner exception.",
            new AuthenticationException("Authentication failed, see inner exception.",
                new System.ComponentModel.Win32Exception(unchecked((int)status), message)));

    [Fact]
    public void Schannel_untrusted_root_points_to_the_machine_store()
    {
        var hint = AgentConnection.ConnectionHint(Schannel(0x80090325, "The certificate chain was issued by an authority that is not trusted."), new Uri("https://localhost"))!;
        Assert.Contains("ylm-root-ca.crt", hint);
        Assert.Contains("Lokaler Computer", hint);
    }

    [Fact]
    public void Schannel_handshake_failure_does_not_claim_missing_https()
    {
        var ex = Schannel(0x80090326, "The message received was unexpected or badly formatted.");
        var hint = AgentConnection.ConnectionHint(ex, new Uri("https://localhost"))!;
        Assert.DoesNotContain("kein HTTPS", hint);
        var text = AgentConnection.Describe(ex);
        Assert.Contains("badly formatted", text);
        Assert.Contains("Win32Exception, 0x80090326", text);
    }

    [Fact]
    public void Wrong_name_mentions_the_host() =>
        Assert.Contains("ylm.firma.local", AgentConnection.ConnectionHint(Schannel(0x80090322, "The target principal name is incorrect."), new Uri("https://ylm.firma.local"))!);
}
