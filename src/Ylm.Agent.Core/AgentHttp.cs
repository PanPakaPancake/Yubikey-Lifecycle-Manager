using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Ylm.Agent.Core;

/// <summary>HTTP-Verbindungen des Agents zum Server (Koppeln, SignalR, Updates) mit einheitlicher Zertifikatsprüfung.</summary>
public static class AgentHttp
{
    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        SslOptions = new SslClientAuthenticationOptions
        {
            // Sperrprüfung online, aber wie Browser "weich": Interne Zertifizierungsstellen (Caddy "tls internal",
            // viele Firmen-PKIs) veröffentlichen keine Sperrliste. Fehlt die Sperrinfo, gilt das Zertifikat trotzdem,
            // ein tatsächlich gesperrtes oder nicht vertrauenswürdiges Zertifikat wird weiterhin abgelehnt.
            CertificateRevocationCheckMode = X509RevocationMode.Online,
            RemoteCertificateValidationCallback = (_, _, chain, errors) => IsAcceptable(errors, chain?.ChainStatus),
        },
    };

    internal static bool IsAcceptable(SslPolicyErrors errors, X509ChainStatus[]? status)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || status is null || status.Length == 0) return false;
        return status.All(s => s.Status is X509ChainStatusFlags.NoError or X509ChainStatusFlags.RevocationStatusUnknown or X509ChainStatusFlags.OfflineRevocation);
    }
}
