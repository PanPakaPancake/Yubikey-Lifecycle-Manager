using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Server.Secrets;
using Ylm.Server.Security;

namespace Ylm.Server.Setup;

/// <summary>Installation setup incl. the installation's own Entra app registration (planning 4.1 step 2, roadmap V1).</summary>
public sealed class SetupService(
    YlmDbContext db,
    ISecretStore secrets,
    AppCertificateService certificates,
    IGraphClientFactory graphFactory,
    IOptionsMonitorCache<OpenIdConnectOptions> oidcCache,
    AuditService audit,
    IOptions<YlmOptions> options,
    ILogger<SetupService> logger)
{
    private const string TokenSecret = "setup-token";

    public static readonly string[] RequiredApplicationPermissions =
    [
        "UserAuthenticationMethod.ReadWrite.All",
        "User.Read.All",
        "GroupMember.Read.All",
        "Policy.Read.All",
        "Policy.ReadWrite.AuthenticationMethod",
        "AuditLog.Read.All",
        "Organization.Read.All",
    ];

    public static readonly string[] DelegatedScopes = ["openid", "profile", "email", "User.Read"];

    public async Task<InstallationSettings> GetSettingsAsync()
    {
        var s = await db.Installation.FirstOrDefaultAsync();
        if (s is not null) return s;
        s = new InstallationSettings();
        db.Installation.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    /// <summary>Creates (once) and logs the setup token that protects the setup wizard.</summary>
    public async Task EnsureSetupTokenAsync()
    {
        var settings = await GetSettingsAsync();
        if (settings.IsSetupComplete) return;
        var token = await secrets.GetAsync(TokenSecret);
        if (token is null)
        {
            token = Encoding.UTF8.GetBytes(TokenHashing.NewSecret(18));
            await secrets.SetAsync(TokenSecret, token);
        }
        logger.LogWarning("Einrichtung noch nicht abgeschlossen. Setup-Token: {Token}", Encoding.UTF8.GetString(token));
    }

    public async Task<bool> ValidateTokenAsync(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var settings = await GetSettingsAsync();
        if (settings.IsSetupComplete) return false;
        var stored = await secrets.GetAsync(TokenSecret);
        return stored is not null && TokenHashing.FixedTimeEquals(Encoding.UTF8.GetString(stored), token.Trim());
    }

    public string BaseUrl(InstallationSettings s, HttpRequest? request) =>
        ((string.IsNullOrWhiteSpace(options.Value.PublicBaseUrl) ? null : options.Value.PublicBaseUrl) ?? s.PublicBaseUrl ?? (request is null ? "https://localhost" : $"{request.Scheme}://{request.Host}")).TrimEnd('/');

    public async Task<InstallationSettings> SaveBasicsAsync(string organizationName, string publicBaseUrl, string bootstrapOwnerUpn)
    {
        if (string.IsNullOrWhiteSpace(organizationName)) throw ApiException.BadRequest("invalid", "Bitte einen Organisationsnamen angeben.");
        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Host != "localhost"))
            throw ApiException.BadRequest("invalid", "Die öffentliche URL muss mit https:// beginnen.");
        if (!bootstrapOwnerUpn.Contains('@')) throw ApiException.BadRequest("invalid", "Bitte den Anmeldenamen (UPN) des ersten Owners angeben.");
        var s = await GetSettingsAsync();
        s.OrganizationName = organizationName.Trim();
        s.PublicBaseUrl = publicBaseUrl.TrimEnd('/');
        s.BootstrapOwnerUpn = bootstrapOwnerUpn.Trim();
        await db.SaveChangesAsync();
        return s;
    }

    public async Task<X509Certificate2> EnsureCertificateAsync()
    {
        var s = await GetSettingsAsync();
        var cert = await certificates.GetAsync();
        if (cert is null)
        {
            cert = await certificates.CreateAsync(s.OrganizationName);
            s.CertificateThumbprint = cert.Thumbprint;
            s.CertificateExpiresAt = cert.NotAfter.ToUniversalTime();
            await db.SaveChangesAsync();
        }
        return cert;
    }

    public IReadOnlyList<string> RedirectUris(InstallationSettings s, HttpRequest? request)
    {
        var b = BaseUrl(s, request);
        return [b + "/signin-oidc", b + "/api/tenants/consent-callback"];
    }

    /// <summary>PowerShell script (Microsoft Graph PowerShell) that creates the multi-tenant app registration.</summary>
    public string BuildAppRegistrationScript(InstallationSettings s, X509Certificate2 cert, HttpRequest? request)
    {
        var certB64 = Convert.ToBase64String(cert.Export(X509ContentType.Cert));
        var redirects = string.Join(", ", RedirectUris(s, request).Select(u => $"'{u}'"));
        var appRoles = string.Join(", ", RequiredApplicationPermissions.Select(p => $"'{p}'"));
        var scopes = string.Join(", ", DelegatedScopes.Select(p => $"'{p}'"));
        var name = ("Yubikey Lifecycle Manager " + s.OrganizationName).Replace("'", "''").Trim();
        return $$"""
            # Erstellt die App-Registrierung für den Yubikey Lifecycle Manager im Heim-Tenant und erteilt den Admin-Consent.
            # Ausführen als Globaler Administrator oder Privileged Role Administrator.
            # Benötigt: Install-Module Microsoft.Graph -Scope CurrentUser
            $ErrorActionPreference = 'Stop'
            Connect-MgGraph -Scopes 'Application.ReadWrite.All', 'AppRoleAssignment.ReadWrite.All', 'DelegatedPermissionGrant.ReadWrite.All' -NoWelcome
            $graphAppId = '00000003-0000-0000-c000-000000000000'
            $graphSp = Get-MgServicePrincipal -Filter "appId eq '$graphAppId'"
            $appRoleIds = @()
            foreach ($p in @({{appRoles}})) {
              $role = $graphSp.AppRoles | Where-Object { $_.Value -eq $p }
              if (-not $role) { throw "Anwendungsberechtigung $p nicht gefunden." }
              $appRoleIds += $role.Id
            }
            $scopeNames = @({{scopes}})
            $access = @($appRoleIds | ForEach-Object { @{ Id = $_; Type = 'Role' } })
            foreach ($p in $scopeNames) {
              $scope = $graphSp.Oauth2PermissionScopes | Where-Object { $_.Value -eq $p }
              if (-not $scope) { throw "Delegierte Berechtigung $p nicht gefunden." }
              $access += @{ Id = $scope.Id; Type = 'Scope' }
            }
            $certBytes = [Convert]::FromBase64String('{{certB64}}')
            $app = New-MgApplication -DisplayName '{{name}}' -SignInAudience 'AzureADMultipleOrgs' `
              -Web @{ RedirectUris = @({{redirects}}) } `
              -RequiredResourceAccess @(@{ ResourceAppId = $graphAppId; ResourceAccess = $access }) `
              -KeyCredentials @(@{ Type = 'AsymmetricX509Cert'; Usage = 'Verify'; Key = $certBytes; DisplayName = 'YLM {{cert.Thumbprint[..8]}}' })
            Write-Host "App-Registrierung angelegt: $($app.AppId)"

            # Dienstprinzipal anlegen. Direkt nach dem Anlegen der App kann Entra noch kurz brauchen.
            $sp = $null
            for ($i = 1; $i -le 6 -and -not $sp; $i++) {
              try { $sp = New-MgServicePrincipal -AppId $app.AppId }
              catch { if ($i -eq 6) { throw }; Start-Sleep -Seconds 5 }
            }
            Write-Host "Dienstprinzipal angelegt: $($sp.Id)"

            # Admin-Consent: Anwendungsberechtigungen zuweisen ...
            foreach ($roleId in $appRoleIds) {
              for ($i = 1; $i -le 6; $i++) {
                try {
                  New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $sp.Id -PrincipalId $sp.Id -ResourceId $graphSp.Id -AppRoleId $roleId | Out-Null
                  break
                }
                catch { if ($i -eq 6) { throw }; Start-Sleep -Seconds 5 }
              }
            }
            # ... und delegierte Berechtigungen für alle Benutzer freigeben.
            New-MgOauth2PermissionGrant -ClientId $sp.Id -ConsentType 'AllPrincipals' -ResourceId $graphSp.Id -Scope ($scopeNames -join ' ') | Out-Null
            Write-Host 'Admin-Consent erteilt.'

            $ctx = Get-MgContext
            Write-Host ''
            Write-Host "Client-ID (Anwendungs-ID): $($app.AppId)"
            Write-Host "Heim-Tenant-ID:           $($ctx.TenantId)"
            Write-Host 'Diese beiden Werte im Setup-Assistenten eintragen.'
            """;
    }

    public async Task<InstallationSettings> SaveAppRegistrationAsync(Guid clientId, Guid homeTenantId)
    {
        var s = await GetSettingsAsync();
        s.ClientId = clientId;
        s.HomeTenantId = homeTenantId;
        await db.SaveChangesAsync();
        (graphFactory as GraphClientFactory)?.Reset();
        return s;
    }

    /// <summary>Tries to get an app-only token for the home tenant to validate client id, tenant and certificate.</summary>
    public async Task<(bool Ok, string Message, IReadOnlyList<string> Granted)> TestAppRegistrationAsync()
    {
        var s = await GetSettingsAsync();
        if (s.ClientId is null || s.HomeTenantId is null) return (false, "Client-ID und Tenant-ID fehlen.", []);
        try
        {
            var graph = await graphFactory.ForTenantAsync(s.HomeTenantId.Value);
            var granted = await graph.GetGrantedApplicationPermissionsAsync();
            return (true, "Anmeldung mit dem App-Zertifikat erfolgreich.", granted);
        }
        catch (Exception ex)
        {
            return (false, "Anmeldung fehlgeschlagen: " + ex.Message, []);
        }
    }

    public async Task CompleteAsync()
    {
        var s = await GetSettingsAsync();
        if (s.ClientId is null || s.HomeTenantId is null || string.IsNullOrEmpty(s.BootstrapOwnerUpn) || await certificates.GetAsync() is null)
            throw ApiException.BadRequest("setup_incomplete", "Bitte zuerst alle Schritte der Einrichtung abschließen.");
        s.SetupCompletedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditService.System, AuditActions.SetupCompleted, details: new { s.OrganizationName, s.ClientId, s.HomeTenantId, s.BootstrapOwnerUpn });
        await db.SaveChangesAsync();
        await secrets.DeleteAsync(TokenSecret);
        oidcCache.TryRemove(AuthSchemes.Entra);
    }
}
