using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Updates;

/// <summary>Betriebsart: mitgelieferter Caddy (HTTPS), eigener Reverse Proxy/ZTNA oder nur lokal auf dem PC.</summary>
public enum HostingMode { Https, Proxy, Local }

/// <summary>Zertifikat von Caddy: interne Zertifizierungsstelle, eigenes Zertifikat, Let's Encrypt (HTTP) oder per DNS-Prüfung.</summary>
public enum HostingTls { Internal, Own, LetsEncrypt, LetsEncryptDns }

/// <summary>Zugangsdaten für den DNS-Anbieter. Leere Geheimnisse = vorhandene behalten.</summary>
public sealed record HostingDns(
    string? Token = null, string? KeyId = null, string? Secret = null, string? Region = null,
    string? AzureTenant = null, string? AzureSubscription = null, string? AzureResourceGroup = null, string? NetcupCustomer = null);

public sealed record HostingRequest(
    HostingMode Mode,
    string? Domain = null,
    string? Aliases = null,
    bool KeepOldAddress = true,
    int? HttpsPort = null,
    int? HttpPort = null,
    string? HttpsBind = null,
    HostingTls Tls = HostingTls.Internal,
    string? AcmeEmail = null,
    string? DnsProvider = null,
    HostingDns? Dns = null,
    string? CertificatePem = null,
    string? KeyPem = null,
    string? PublicUrl = null,
    string? BindAddress = null,
    int? Port = null);

/// <summary>Aktueller Stand aus .env, wie ihn der Updater liefert.</summary>
public sealed record HostingCurrent(IReadOnlyDictionary<string, string> Env, IReadOnlySet<string> SecretsSet, IReadOnlySet<string> Files);

public sealed record HostingView(
    HostingMode Mode, string? PublicUrl, string? Domain, string? Aliases, int HttpsPort, int HttpPort, string? HttpsBind,
    HostingTls Tls, string? AcmeEmail, string? DnsProvider, HostingDns Dns, IReadOnlyList<string> SecretsSet,
    bool OwnCertificatePresent, string? BindAddress, int Port);

public sealed record HostingChange(IReadOnlyDictionary<string, string?> Env, IReadOnlyDictionary<string, string> Files, string PublicUrl);

/// <summary>Übersetzt zwischen .env (Installationsskript, Compose, Caddyfile) und den Einstellungen der Oberfläche.</summary>
public static partial class HostingPlan
{
    public sealed record DnsProviderInfo(string Id, string Name, string[] Required);

    /// <summary>DNS-Anbieter im Caddy-Image (caddy/Dockerfile) und Snippets im Caddyfile (dns-&lt;id&gt;).</summary>
    public static readonly IReadOnlyList<DnsProviderInfo> DnsProviders =
    [
        new("cloudflare", "Cloudflare", ["token"]),
        new("hetzner", "Hetzner", ["token"]),
        new("ionos", "IONOS", ["token"]),
        new("netcup", "netcup", ["netcupCustomer", "keyId", "secret"]),
        new("azure", "Azure DNS", ["azureTenant", "azureSubscription", "azureResourceGroup", "keyId", "secret"]),
        new("route53", "AWS Route 53", ["keyId", "secret", "region"]),
        new("digitalocean", "DigitalOcean", ["token"]),
    ];

    private static readonly Dictionary<string, string> DnsEnv = new()
    {
        ["token"] = "YLM_DNS_TOKEN", ["keyId"] = "YLM_DNS_KEY_ID", ["secret"] = "YLM_DNS_SECRET", ["region"] = "YLM_DNS_REGION",
        ["azureTenant"] = "YLM_DNS_AZURE_TENANT", ["azureSubscription"] = "YLM_DNS_AZURE_SUBSCRIPTION",
        ["azureResourceGroup"] = "YLM_DNS_AZURE_RESOURCE_GROUP", ["netcupCustomer"] = "YLM_DNS_NETCUP_CUSTOMER",
    };

    private static readonly HashSet<string> SecretFields = ["token", "secret"];

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex HostRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"^[^\x00-\x1f\x7f$'""\\]*$")]
    private static partial Regex SafeValue();

    public static HostingView Describe(HostingCurrent current, string? fallbackUrl)
    {
        string? Get(string k) => current.Env.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        int Int(string k, int d) => int.TryParse(Get(k), out var v) ? v : d;
        var profiles = Get("COMPOSE_PROFILES") ?? "";
        var url = Get("YLM_PUBLIC_URL") ?? fallbackUrl;
        var mode = profiles.Split(',').Any(p => p.Trim() == "https") ? HostingMode.Https
            : url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.IsLoopback ? HostingMode.Local
            : HostingMode.Proxy;
        var tlsRaw = (Get("YLM_CADDY_TLS") ?? "").Trim();
        string? email = Get("YLM_ACME_EMAIL"), provider = null;
        HostingTls tls;
        if (tlsRaw == "tls internal") tls = HostingTls.Internal;
        else if (tlsRaw.StartsWith("tls /", StringComparison.Ordinal)) tls = HostingTls.Own;
        else if (tlsRaw.StartsWith("import dns-", StringComparison.Ordinal)) { tls = HostingTls.LetsEncryptDns; provider = tlsRaw["import dns-".Length..]; }
        else { tls = HostingTls.LetsEncrypt; if (tlsRaw.StartsWith("tls ", StringComparison.Ordinal)) email = tlsRaw[4..].Trim(); }
        var bind = Get("YLM_BIND") ?? "127.0.0.1:8080";
        var colon = bind.LastIndexOf(':');
        var bindAddress = colon > 0 ? bind[..colon] : "127.0.0.1";
        var port = colon > 0 && int.TryParse(bind[(colon + 1)..], out var p) ? p : 8080;
        var dns = new HostingDns(
            KeyId: Get("YLM_DNS_KEY_ID"), Region: Get("YLM_DNS_REGION"), AzureTenant: Get("YLM_DNS_AZURE_TENANT"),
            AzureSubscription: Get("YLM_DNS_AZURE_SUBSCRIPTION"), AzureResourceGroup: Get("YLM_DNS_AZURE_RESOURCE_GROUP"),
            NetcupCustomer: Get("YLM_DNS_NETCUP_CUSTOMER"));
        return new HostingView(mode, url, Get("YLM_DOMAIN"), Get("YLM_DOMAIN_ALIASES"), Int("YLM_HTTPS_PORT", 443), Int("YLM_HTTP_PORT", 80),
            Get("YLM_HTTPS_BIND"), tls, email, provider, dns,
            current.SecretsSet.Select(k => DnsEnv.First(e => e.Value == k).Key).ToList(),
            current.Files.Contains("ylm.crt") && current.Files.Contains("ylm.key"), bindAddress, port);
    }

    public static HostingChange Build(HostingCurrent current, HostingRequest r)
    {
        var env = new Dictionary<string, string?>();
        var files = new Dictionary<string, string>();
        string url;
        switch (r.Mode)
        {
            case HostingMode.Https:
            {
                var domain = NormalizeHost(r.Domain) ?? throw ApiException.BadRequest("invalid_domain", "Bitte einen gültigen DNS-Namen angeben, z. B. ylm.firma.de.");
                var httpsPort = Port(r.HttpsPort ?? 443, "HTTPS-Port");
                var httpPort = Port(r.HttpPort ?? 80, "HTTP-Port");
                if (httpsPort == httpPort) throw ApiException.BadRequest("invalid_port", "HTTP- und HTTPS-Port müssen verschieden sein.");
                url = "https://" + domain + (httpsPort == 443 ? "" : ":" + httpsPort);

                var aliases = (r.Aliases ?? "").Split([' ', ',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(a => NormalizeHost(a) ?? throw ApiException.BadRequest("invalid_alias", $"Ungültiger weiterer Name: {a}")).ToList();
                var oldDomain = current.Env.GetValueOrDefault("YLM_DOMAIN");
                if (r.KeepOldAddress && !string.IsNullOrEmpty(oldDomain) && !string.Equals(oldDomain, domain, StringComparison.OrdinalIgnoreCase))
                    aliases.Add(oldDomain.ToLowerInvariant());
                aliases = aliases.Where(a => a != domain).Distinct().ToList();

                env["COMPOSE_PROFILES"] = "https";
                env["YLM_DOMAIN"] = domain;
                env["YLM_DOMAIN_ALIASES"] = aliases.Count == 0 ? null : string.Join(" ", aliases);
                env["YLM_HTTPS_PORT"] = httpsPort == 443 ? null : httpsPort.ToString();
                env["YLM_HTTP_PORT"] = httpPort == 80 ? null : httpPort.ToString();
                if (r.HttpsBind is not null)
                    env["YLM_HTTPS_BIND"] = string.IsNullOrWhiteSpace(r.HttpsBind) ? null : BindAddress(r.HttpsBind);
                else if (domain == "localhost")
                    env["YLM_HTTPS_BIND"] = "127.0.0.1"; // nur dieser PC: Caddy nicht im Netz anbieten
                else if (current.Env.GetValueOrDefault("YLM_HTTPS_BIND") == "127.0.0.1" && current.Env.GetValueOrDefault("YLM_DOMAIN") == "localhost")
                    env["YLM_HTTPS_BIND"] = null; // von localhost auf einen echten Namen: wieder im Netz erreichbar

                if (domain == "localhost" && r.Tls is HostingTls.LetsEncrypt or HostingTls.LetsEncryptDns)
                    throw ApiException.BadRequest("acme_localhost", "Für localhost stellt Let's Encrypt kein Zertifikat aus. Bitte die interne Zertifizierungsstelle wählen.");
                var email = string.IsNullOrWhiteSpace(r.AcmeEmail) ? null : r.AcmeEmail.Trim();
                if (email is not null && !EmailRegex().IsMatch(email)) throw ApiException.BadRequest("invalid_email", "Bitte eine gültige E-Mail-Adresse für Let's Encrypt angeben.");
                env["YLM_ACME_EMAIL"] = email;
                switch (r.Tls)
                {
                    case HostingTls.Internal:
                        env["YLM_CADDY_TLS"] = "tls internal"; env["YLM_MODE"] = "internal"; break;
                    case HostingTls.Own:
                        OwnCertificate(current, r, domain, aliases, files);
                        env["YLM_CADDY_TLS"] = "tls /certs/ylm.crt /certs/ylm.key"; env["YLM_MODE"] = "cert"; break;
                    case HostingTls.LetsEncrypt:
                        if (httpPort != 80)
                            throw ApiException.BadRequest("acme_port", "Let's Encrypt ohne DNS-Prüfung braucht Port 80 aus dem Internet. Bei anderen Ports bitte „Let's Encrypt per DNS“ wählen.");
                        env["YLM_CADDY_TLS"] = email is null ? "" : "tls " + email; env["YLM_MODE"] = "https"; break;
                    case HostingTls.LetsEncryptDns:
                        Dns(current, r, env);
                        env["YLM_MODE"] = "https"; break;
                    default:
                        throw ApiException.BadRequest("invalid_tls", "Unbekannte Zertifikatsart.");
                }
                break;
            }
            case HostingMode.Proxy:
            {
                if (!Uri.TryCreate(r.PublicUrl?.Trim(), UriKind.Absolute, out var u) || u.Scheme != "https" || NormalizeHost(u.Host) is null || u.AbsolutePath != "/")
                    throw ApiException.BadRequest("invalid_url", "Bitte die Adresse mit https:// angeben, ohne Pfad, z. B. https://ylm.firma.de.");
                url = u.GetLeftPart(UriPartial.Authority);
                env["COMPOSE_PROFILES"] = "";
                env["YLM_BIND"] = BindAddress(r.BindAddress ?? "127.0.0.1") + ":" + Port(r.Port ?? 8080, "Port");
                env["YLM_MODE"] = "proxy";
                break;
            }
            case HostingMode.Local:
            {
                var port = Port(r.Port ?? 8080, "Port");
                url = "http://localhost:" + port;
                env["COMPOSE_PROFILES"] = "";
                env["YLM_BIND"] = "127.0.0.1:" + port;
                env["YLM_MODE"] = "local";
                break;
            }
            default:
                throw ApiException.BadRequest("invalid_mode", "Unbekannte Betriebsart.");
        }
        env["YLM_PUBLIC_URL"] = url;
        foreach (var (k, v) in env)
            if (v is not null && (v.Length > 2000 || !SafeValue().IsMatch(v)))
                throw ApiException.BadRequest("invalid_value", $"Ungültiges Zeichen in {k}. Nicht erlaubt sind $, Anführungszeichen und Backslash.");
        // Nur geänderte Werte schicken.
        static string? Norm(string? v) => string.IsNullOrEmpty(v) ? null : v;
        var changed = env.Where(e => Norm(current.Env.GetValueOrDefault(e.Key)) != Norm(e.Value)).ToDictionary(e => e.Key, e => e.Value);
        return new HostingChange(changed, files, url);
    }

    private static void Dns(HostingCurrent current, HostingRequest r, Dictionary<string, string?> env)
    {
        var provider = DnsProviders.FirstOrDefault(p => p.Id == r.DnsProvider)
                       ?? throw ApiException.BadRequest("invalid_dns_provider", "Bitte einen DNS-Anbieter auswählen.");
        var dns = r.Dns ?? new HostingDns();
        var values = new Dictionary<string, string?>
        {
            ["token"] = dns.Token, ["keyId"] = dns.KeyId, ["secret"] = dns.Secret, ["region"] = dns.Region,
            ["azureTenant"] = dns.AzureTenant, ["azureSubscription"] = dns.AzureSubscription,
            ["azureResourceGroup"] = dns.AzureResourceGroup, ["netcupCustomer"] = dns.NetcupCustomer,
        };
        var sameProvider = current.Env.GetValueOrDefault("YLM_DNS_PROVIDER") == provider.Id;
        foreach (var (field, envKey) in DnsEnv)
        {
            var value = values[field]?.Trim();
            var required = provider.Required.Contains(field);
            if (string.IsNullOrEmpty(value))
            {
                // Geheimnisse bleiben, wenn sie schon gesetzt sind und der Anbieter gleich bleibt.
                var keep = SecretFields.Contains(field) && sameProvider && current.SecretsSet.Contains(envKey);
                if (required && !keep) throw ApiException.BadRequest("dns_missing", $"Für {provider.Name} fehlt: {Label(field)}.");
                if (!keep) env[envKey] = null;
            }
            else env[envKey] = required ? value : null;
        }
        env["YLM_DNS_PROVIDER"] = provider.Id;
        env["YLM_CADDY_TLS"] = "import dns-" + provider.Id;
    }

    private static string Label(string field) => field switch
    {
        "token" => "API-Token", "keyId" => "Schlüssel-ID bzw. Client-ID", "secret" => "Geheimnis bzw. Passwort", "region" => "Region",
        "azureTenant" => "Tenant-ID", "azureSubscription" => "Subscription-ID", "azureResourceGroup" => "Ressourcengruppe",
        "netcupCustomer" => "Kundennummer", _ => field,
    };

    private static void OwnCertificate(HostingCurrent current, HostingRequest r, string domain, List<string> aliases, Dictionary<string, string> files)
    {
        var hasNew = !string.IsNullOrWhiteSpace(r.CertificatePem) || !string.IsNullOrWhiteSpace(r.KeyPem);
        if (!hasNew)
        {
            if (current.Files.Contains("ylm.crt") && current.Files.Contains("ylm.key")) return;
            throw ApiException.BadRequest("certificate_missing", "Bitte Zertifikat und privaten Schlüssel (PEM) hochladen.");
        }
        if (string.IsNullOrWhiteSpace(r.CertificatePem) || string.IsNullOrWhiteSpace(r.KeyPem))
            throw ApiException.BadRequest("certificate_missing", "Bitte Zertifikat und privaten Schlüssel zusammen hochladen.");
        X509Certificate2 cert;
        try
        {
            cert = X509Certificate2.CreateFromPem(r.CertificatePem, r.KeyPem);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            throw ApiException.BadRequest("certificate_invalid", "Zertifikat und Schlüssel passen nicht zusammen oder sind kein PEM (Schlüssel ohne Passwort): " + e.Message);
        }
        using (cert)
        {
            if (cert.NotAfter < DateTime.Now) throw ApiException.BadRequest("certificate_expired", $"Das Zertifikat ist am {cert.NotAfter:dd.MM.yyyy} abgelaufen.");
            foreach (var name in aliases.Prepend(domain))
                if (!cert.MatchesHostname(name, allowWildcards: true, allowCommonName: true))
                    throw ApiException.BadRequest("certificate_name", $"Das Zertifikat gilt nicht für {name} ({cert.Subject}).");
        }
        files["ylm.crt"] = r.CertificatePem.Trim() + "\n";
        files["ylm.key"] = r.KeyPem.Trim() + "\n";
    }

    private static int Port(int port, string label) =>
        port is >= 1 and <= 65535 ? port : throw ApiException.BadRequest("invalid_port", $"{label}: bitte eine Zahl von 1 bis 65535.");

    private static string BindAddress(string address)
    {
        var a = address.Trim();
        return System.Net.IPAddress.TryParse(a.Trim('[', ']'), out _) ? a
            : throw ApiException.BadRequest("invalid_bind", $"Ungültige IP-Adresse: {a}. Beispiele: 127.0.0.1, 0.0.0.0, 10.0.0.5.");
    }

    public static string? NormalizeHost(string? host)
    {
        var h = (host ?? "").Trim().ToLowerInvariant();
        if (h.StartsWith("https://")) h = h[8..];
        h = h.TrimEnd('/');
        return HostRegex().IsMatch(h) && h.Contains('.') || h == "localhost" ? h : null;
    }
}
