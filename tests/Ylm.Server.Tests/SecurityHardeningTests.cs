using System.Net;
using Ylm.Server.Security;

namespace Ylm.Server.Tests;

public sealed class SecurityHardeningTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/keys?status=Active", true)]
    [InlineData("/tenants/1#x", true)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("https://evil.example", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/\tx", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Return_url_must_be_a_local_path(string? url, bool expected) =>
        Assert.Equal(expected, AuthEndpoints.IsLocalPath(url));

    [Theory]
    [InlineData("172.18.0.5", true)]
    [InlineData("::ffff:172.18.0.5", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.65.1", true)]
    [InlineData("::1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    public void Default_proxy_trust_covers_only_private_networks(string address, bool trusted)
    {
        var ip = IPAddress.Parse(address);
        Assert.Equal(trusted, ProxyTrust.Networks([]).Any(n => n.Contains(ip)));
    }

    [Fact]
    public void Configured_proxies_replace_the_default()
    {
        var nets = ProxyTrust.Networks(["203.0.113.10, 2001:db8::/32", ""]).ToList();
        Assert.Contains(nets, n => n.Contains(IPAddress.Parse("203.0.113.10")));
        Assert.Contains(nets, n => n.Contains(IPAddress.Parse("2001:db8::5")));
        Assert.DoesNotContain(nets, n => n.Contains(IPAddress.Parse("10.0.0.1")));
        Assert.Throws<InvalidOperationException>(() => ProxyTrust.Networks(["kein-netz"]).ToList());
    }

    [Fact]
    public void Empty_setting_from_compose_keeps_the_default()
    {
        // docker-compose setzt Ylm__TrustedProxies__0 immer, ohne Wert ist es "".
        var nets = ProxyTrust.Networks([""]).ToList();
        Assert.Contains(nets, n => n.Contains(IPAddress.Parse("10.0.0.1")));
        Assert.Contains(nets, n => n.Contains(IPAddress.Parse("172.18.0.3")));
    }
}

public sealed class SecurityHeaderTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var res = await _app.Client().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Agent_hub_with_invalid_token_answers_401_not_login_redirect()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/hubs/agent/negotiate?negotiateVersion=1");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "ylma_00000000000000000000000000000000_ungueltig");
        var res = await _app.Client().SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
