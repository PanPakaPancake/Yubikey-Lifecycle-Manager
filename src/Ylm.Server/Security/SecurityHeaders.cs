using System.Net;

namespace Ylm.Server.Security;

/// <summary>
/// Grundlegende Sicherheits-Header für alle Antworten, auch ohne vorgeschalteten Proxy:
/// keine Einbettung in fremde Seiten (Clickjacking), kein MIME-Sniffing, Referrer nur innerhalb der Plattform.
/// </summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    public const string AuthRateLimit = "auth";

    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            if (!h.ContainsKey("Content-Security-Policy")) h.ContentSecurityPolicy = "frame-ancestors 'none'";
            h["Referrer-Policy"] = "same-origin";
            return Task.CompletedTask;
        });
        return next(ctx);
    }
}

/// <summary>Netze, deren Forwarded-Header die Plattform übernimmt.</summary>
public static class ProxyTrust
{
    /// <summary>Loopback und private Adressbereiche: Docker-Netze, Caddy, Reverse Proxy im internen Netz.</summary>
    public static readonly string[] Default =
    [
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10",
        "::1/128", "fc00::/7", "fe80::/10",
    ];

    public static IEnumerable<System.Net.IPNetwork> Networks(IReadOnlyCollection<string> configured)
    {
        foreach (var entry in configured.Count == 0 ? Default : configured)
        {
            var value = entry.Trim();
            if (value.Length == 0) continue;
            if (!value.Contains('/') && IPAddress.TryParse(value, out var single))
                value += single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "/128" : "/32";
            if (!System.Net.IPNetwork.TryParse(value, out var network))
                throw new InvalidOperationException($"Ylm:TrustedProxies: \"{entry}\" ist keine gültige IP-Adresse oder kein gültiges Netz.");
            yield return network;
            // IPv4-Absender kommen bei Dual-Stack-Sockets als ::ffff:a.b.c.d an.
            if (network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                yield return new System.Net.IPNetwork(network.BaseAddress.MapToIPv6(), network.PrefixLength + 96);
        }
    }
}
