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
using Ylm.Server.Tenants;

namespace Ylm.Server.Setup;

/// <summary>Installation setup incl. the installation's own Entra app registration (planning 4.1 step 2, roadmap V1).</summary>
public sealed class SetupService(
    YlmDbContext db,
    ISecretStore secrets,
    AppCertificateService certificates,
    IGraphClientFactory graphFactory,
    IOptionsMonitorCache<OpenIdConnectOptions> oidcCache,
    AuditService audit,
    IEntraBootstrap bootstrap,
    SetupSignInStore signIns,
    TenantCheckService checks,
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

    public async Task<InstallationSettings> SaveBasicsAsync(string organizationName, string publicBaseUrl, string? bootstrapOwnerUpn)
    {
        if (string.IsNullOrWhiteSpace(organizationName)) throw ApiException.BadRequest("invalid", "Bitte einen Organisationsnamen angeben.");
        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Host != "localhost"))
            throw ApiException.BadRequest("invalid", "Die öffentliche URL muss mit https:// beginnen.");
        if (!string.IsNullOrWhiteSpace(bootstrapOwnerUpn) && !bootstrapOwnerUpn.Contains('@'))
            throw ApiException.BadRequest("invalid", "Bitte den Anmeldenamen (UPN) des ersten Owners angeben.");
        var s = await GetSettingsAsync();
        s.OrganizationName = organizationName.Trim();
        s.PublicBaseUrl = publicBaseUrl.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(bootstrapOwnerUpn)) s.BootstrapOwnerUpn = bootstrapOwnerUpn.Trim();
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
        if (s.ClientId != clientId) s.ApplicationObjectId = null;
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
            // Always a fresh token: the roles claim is fixed when the token is issued. A token fetched right after
            // provisioning (before Entra replicated the consent) would otherwise be reused for up to an hour.
            graphFactory.Invalidate(s.HomeTenantId.Value);
            var graph = await graphFactory.ForTenantAsync(s.HomeTenantId.Value);
            var granted = await graph.GetGrantedApplicationPermissionsAsync();
            return (true, "Anmeldung mit dem App-Zertifikat erfolgreich.", granted);
        }
        catch (Exception ex)
        {
            return (false, "Anmeldung fehlgeschlagen: " + ex.Message, []);
        }
    }

    // ---- Sign-in with Microsoft and automatic app registration (no script needed) ----

    public sealed record SignInPollResult(string Status, string? Message = null, SignedInAdmin? Admin = null);

    public async Task<SetupSignInStore.PendingDeviceCode> StartSignInAsync(CancellationToken ct = default)
    {
        var start = await bootstrap.StartDeviceCodeAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var pending = new SetupSignInStore.PendingDeviceCode(start.DeviceCode, start.UserCode, start.VerificationUri,
            now.AddSeconds(start.ExpiresInSeconds), Math.Max(1, start.IntervalSeconds), now.AddSeconds(Math.Max(1, start.IntervalSeconds)));
        signIns.Pending = pending;
        return pending;
    }

    /// <summary>
    /// Interactive sign-in (authorization code + PKCE). Microsoft redirects to http://localhost: reached directly when the
    /// platform itself runs on localhost, otherwise the admin copies the address from the browser back into the assistant.
    /// </summary>
    public (string Url, string RedirectUri, bool Automatic) StartInteractiveSignIn(HttpRequest request, bool allowAutomatic = true)
    {
        var host = request.Host.Host;
        var automatic = allowAutomatic && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) && request.Scheme == "http";
        var redirectUri = automatic && request.Host.Port is { } port ? $"http://localhost:{port}" : "http://localhost";
        var state = Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var verifier = Base64Url(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));
        signIns.Interactive = new SetupSignInStore.PendingInteractive(state, verifier, redirectUri, DateTimeOffset.UtcNow.AddMinutes(15));
        return (bootstrap.BuildAuthorizeUrl(redirectUri, state, challenge), redirectUri, automatic);
    }

    /// <summary>Completes the interactive sign-in with the redirect address (or code and state taken from it).</summary>
    public async Task<SignInPollResult> CompleteInteractiveSignInAsync(string? redirectedUrl, string? code, string? state, CancellationToken ct = default)
    {
        string? error = null, errorDescription = null;
        if (!string.IsNullOrWhiteSpace(redirectedUrl))
        {
            var text = redirectedUrl.Trim();
            var query = text.Contains('?') ? text[(text.IndexOf('?') + 1)..] : text;
            var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query.Split('#')[0]);
            code ??= parsed.TryGetValue("code", out var c) ? c.ToString() : null;
            state ??= parsed.TryGetValue("state", out var st) ? st.ToString() : null;
            error = parsed.TryGetValue("error", out var er) ? er.ToString() : null;
            errorDescription = parsed.TryGetValue("error_description", out var ed) ? ed.ToString() : null;
        }
        if (!string.IsNullOrEmpty(error))
            return new SignInPollResult("failed", $"Microsoft hat die Anmeldung abgelehnt: {errorDescription ?? error}");
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return new SignInPollResult("failed", "In der Adresse fehlen code und state. Bitte die komplette Adresse aus der Adresszeile kopieren (beginnt mit http://localhost/?code=).");
        var pending = signIns.TakeInteractive(state);
        if (pending is null)
            return new SignInPollResult("expired", "Dieser Anmeldevorgang ist abgelaufen oder gehört zu einem anderen Versuch. Bitte neu starten.");
        var redeemed = await bootstrap.RedeemCodeAsync(code, pending.RedirectUri, pending.CodeVerifier, ct);
        if (redeemed.Status != DeviceCodeStatus.SignedIn)
            return new SignInPollResult("failed", "Die Anmeldung ist fehlgeschlagen: " + redeemed.Error);
        return await SignedInAsync(redeemed, ct);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<SignInPollResult> SignedInAsync(DeviceCodePoll poll, CancellationToken ct)
    {
        var admin = await bootstrap.GetSignedInAdminAsync(poll.AccessToken!, ct);
        signIns.Pending = null;
        signIns.Current = new SetupSignInStore.SignIn(poll.AccessToken!, poll.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30), admin);
        var s = await GetSettingsAsync();
        if (string.IsNullOrEmpty(s.BootstrapOwnerUpn) && admin.Upn.Contains('@'))
        {
            s.BootstrapOwnerUpn = admin.Upn;
            await db.SaveChangesAsync(ct);
        }
        logger.LogInformation("Setup: {Upn} im Tenant {TenantId} bei Microsoft angemeldet", admin.Upn, admin.TenantId);
        return new SignInPollResult("signed_in", Admin: admin);
    }

    public async Task<SignInPollResult> PollSignInAsync(CancellationToken ct = default)
    {
        if (signIns.Current is { } current) return new SignInPollResult("signed_in", Admin: current.Admin);
        var pending = signIns.Pending;
        if (pending is null && signIns.Interactive is not null) return new SignInPollResult("pending");
        if (pending is null) return new SignInPollResult("expired", "Kein Anmeldevorgang aktiv oder der Code ist abgelaufen. Bitte neu starten.");
        if (DateTimeOffset.UtcNow < pending.NextPollAt) return new SignInPollResult("pending");

        var poll = await bootstrap.PollDeviceCodeAsync(pending.DeviceCode, ct);
        switch (poll.Status)
        {
            case DeviceCodeStatus.Pending:
                signIns.Pending = pending with { NextPollAt = DateTimeOffset.UtcNow.AddSeconds(pending.IntervalSeconds) };
                return new SignInPollResult("pending");
            case DeviceCodeStatus.SlowDown:
                var interval = pending.IntervalSeconds + 5;
                signIns.Pending = pending with { IntervalSeconds = interval, NextPollAt = DateTimeOffset.UtcNow.AddSeconds(interval) };
                return new SignInPollResult("pending");
            case DeviceCodeStatus.SignedIn:
                return await SignedInAsync(poll, ct);
            default:
                signIns.Pending = null;
                return new SignInPollResult(poll.Status == DeviceCodeStatus.Declined ? "declined" : poll.Status == DeviceCodeStatus.Expired ? "expired" : "failed",
                    poll.Error ?? "Die Anmeldung ist fehlgeschlagen.");
        }
    }

    /// <summary>Creates certificate, app registration, service principal and admin consent in the signed-in admin's tenant.</summary>
    public async Task<AppProvisioningResult> ProvisionAppAsync(CancellationToken ct = default)
    {
        var signIn = signIns.Current
                     ?? throw ApiException.Conflict("signin_required", "Bitte zuerst bei Microsoft anmelden (die Anmeldung ist abgelaufen oder fehlt).");
        var s = await GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(s.OrganizationName) || (string.IsNullOrWhiteSpace(s.PublicBaseUrl) && string.IsNullOrWhiteSpace(options.Value.PublicBaseUrl)))
            throw ApiException.BadRequest("setup_incomplete", "Bitte zuerst die Grunddaten speichern.");
        var cert = await EnsureCertificateAsync();
        // Reuse the app of an earlier attempt only if it belongs to the same tenant.
        var existing = s.HomeTenantId == signIn.Admin.TenantId ? s.ClientId : null;
        var result = await bootstrap.ProvisionAppAsync(signIn.AccessToken, new AppProvisioningRequest(
            existing,
            ("Yubikey Lifecycle Manager " + s.OrganizationName).Trim(),
            RedirectUris(s, null),
            cert.Export(X509ContentType.Cert),
            "YLM " + cert.Thumbprint[..8],
            RequiredApplicationPermissions,
            DelegatedScopes), ct);

        s.ClientId = result.ClientId;
        s.HomeTenantId = signIn.Admin.TenantId;
        s.ApplicationObjectId = result.ApplicationObjectId;
        if (string.IsNullOrEmpty(s.BootstrapOwnerUpn) && signIn.Admin.Upn.Contains('@')) s.BootstrapOwnerUpn = signIn.Admin.Upn;
        audit.Add(AuditService.System, AuditActions.SetupAppProvisioned, details: new
        {
            clientId = result.ClientId, homeTenantId = signIn.Admin.TenantId, by = signIn.Admin.Upn, steps = result.Steps,
        });
        await db.SaveChangesAsync(ct);
        (graphFactory as GraphClientFactory)?.Reset();
        return result;
    }

    public SignedInAdmin? LastSignedInAdmin => signIns.LastAdmin;

    public sealed record CompleteOptions(string? BootstrapOwnerUpn, bool AddHomeTenant);

    public async Task CompleteAsync(CompleteOptions? request = null, CancellationToken ct = default)
    {
        request ??= new CompleteOptions(null, true);
        var s = await GetSettingsAsync();
        if (!string.IsNullOrWhiteSpace(request.BootstrapOwnerUpn))
        {
            if (!request.BootstrapOwnerUpn.Contains('@')) throw ApiException.BadRequest("invalid", "Bitte den Anmeldenamen (UPN) des ersten Owners angeben.");
            s.BootstrapOwnerUpn = request.BootstrapOwnerUpn.Trim();
        }
        if (string.IsNullOrEmpty(s.BootstrapOwnerUpn))
            throw ApiException.BadRequest("invalid", "Bitte den Anmeldenamen (UPN) des ersten Owners angeben.");
        if (s.ClientId is null || s.HomeTenantId is null || await certificates.GetAsync(ct) is null)
            throw ApiException.BadRequest("setup_incomplete", "Bitte zuerst alle Schritte der Einrichtung abschließen.");

        Tenant? homeTenant = null;
        if (request.AddHomeTenant) homeTenant = await AddHomeTenantAsync(s, ct);

        s.SetupCompletedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditService.System, AuditActions.SetupCompleted, details: new { s.OrganizationName, s.ClientId, s.HomeTenantId, s.BootstrapOwnerUpn, homeTenantAdded = homeTenant is not null });
        await db.SaveChangesAsync(ct);
        await secrets.DeleteAsync(TokenSecret);
        signIns.Clear();
        oidcCache.TryRemove(AuthSchemes.Entra);

        if (homeTenant is not null)
        {
            try { await checks.RunAsync(homeTenant, AuditService.System, ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Tenant-Prüfung für den Heim-Tenant fehlgeschlagen; sie kann in der Oberfläche wiederholt werden."); }
        }
    }

    /// <summary>Registers the home tenant (where the app lives and consent is already granted) as the first managed tenant.</summary>
    private async Task<Tenant?> AddHomeTenantAsync(InstallationSettings s, CancellationToken ct)
    {
        var entraTenantId = s.HomeTenantId!.Value;
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.EntraTenantId == entraTenantId, ct);
        if (tenant is not null) return null;
        var admin = signIns.LastAdmin is { } a && a.TenantId == entraTenantId ? a : null;
        tenant = new Tenant
        {
            EntraTenantId = entraTenantId,
            SyncIntervalMinutes = options.Value.DefaultSyncIntervalMinutes,
            Status = TenantStatus.Active,
            ConsentedAt = DateTimeOffset.UtcNow,
            ConsentedBy = admin?.Upn ?? s.BootstrapOwnerUpn,
            DisplayName = admin?.OrganizationName ?? "",
            DefaultDomain = admin?.DefaultDomain,
        };
        try
        {
            var org = await (await graphFactory.ForTenantAsync(entraTenantId, ct)).GetOrganizationAsync(ct);
            tenant.DisplayName = org.DisplayName;
            tenant.DefaultDomain = org.DefaultDomain;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Organisationsdaten des Heim-Tenants konnten nicht gelesen werden.");
        }
        if (string.IsNullOrWhiteSpace(tenant.DisplayName)) tenant.DisplayName = entraTenantId.ToString();
        db.Tenants.Add(tenant);
        audit.Add(AuditService.System, AuditActions.TenantOnboarded, tenant.Id, target: tenant.DisplayName,
            details: new { entraTenantId, isNew = true, homeTenant = true, by = tenant.ConsentedBy });
        return tenant;
    }
}
