using System.Net;
using System.Net.Sockets;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Integrations;

/// <summary>
/// Webhooks dürfen keine link-lokalen Adressen erreichen (169.254.0.0/16, fe80::/10), vor allem nicht die
/// Metadaten-Schnittstelle von Cloud-Servern (169.254.169.254), die dort Zugangsdaten herausgibt.
/// Geprüft wird beim Verbindungsaufbau nach der DNS-Auflösung, damit auch ein Name, der auf so eine Adresse zeigt, nicht durchkommt.
/// Interne Ziele im Firmennetz (z. B. n8n auf demselben Server) bleiben erlaubt.
/// </summary>
public static class WebhookTargets
{
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            var allowed = addresses.Where(a => !IsBlocked(a)).ToArray();
            if (allowed.Length == 0)
                throw new HttpRequestException(L.F($"Ziel {host} ist nicht erlaubt (link-lokale Adresse, z. B. Cloud-Metadaten)."));
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return (b[0] == 169 && b[1] == 254) || address.Equals(IPAddress.Any);
        }
        return address.IsIPv6LinkLocal || address.Equals(IPAddress.IPv6Any);
    }
}
