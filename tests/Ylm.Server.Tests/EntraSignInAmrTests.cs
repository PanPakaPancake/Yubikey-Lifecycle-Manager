using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;

namespace Ylm.Server.Tests;

/// <summary>Prüfung der Anmeldemethode (amr) bei der Entra-Anmeldung.</summary>
public sealed class EntraSignInAmrTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly Guid _home = Guid.NewGuid();
    private readonly Guid _oid = Guid.NewGuid();

    public void Dispose() => _app.Dispose();

    private Task<(ClaimsPrincipal? Result, string? Error, string? Amr)> SignInAsync(string[] accepted, params string[] amr) =>
        SignInAsync(true, accepted, amr);

    private async Task<(ClaimsPrincipal? Result, string? Error, string? Amr)> SignInAsync(bool? require, string[] accepted, params string[] amr)
    {
        await _app.WithDbAsync(async db =>
        {
            var s = await db.Installation.FirstAsync();
            s.HomeTenantId = _home;
            if (!await db.PlatformUsers.AnyAsync(u => u.Upn == "timo@home.example"))
                db.PlatformUsers.Add(new PlatformUser { Upn = "timo@home.example", DisplayName = "Timo", Role = PlatformRole.Owner, AllTenants = true });
            await db.SaveChangesAsync();
        });
        using var scope = _app.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<YlmOptions>>().Value;
        if (require is { } r) options.RequirePhishingResistantSignIn = r;
        options.PhishingResistantAmrValues = accepted;
        var signIn = scope.ServiceProvider.GetRequiredService<PlatformSignIn>();
        var claims = new List<Claim> { new("tid", _home.ToString()), new("oid", _oid.ToString()), new("preferred_username", "timo@home.example") };
        claims.AddRange(amr.Select(a => new Claim("amr", a)));
        var result = await signIn.MapEntraPrincipalAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")));
        return (result, signIn.LastError, signIn.LastAmr);
    }

    [Fact]
    public async Task Method_is_not_checked_by_default()
    {
        // Standard: welche Methoden gelten, regelt Entra (Bedingter Zugriff), nicht die Plattform.
        Assert.NotNull((await SignInAsync(null, ["fido"], "pwd", "mfa")).Result);
    }

    [Fact]
    public async Task Only_fido_is_accepted_by_default_and_the_rejected_method_is_reported()
    {
        Assert.NotNull((await SignInAsync(["fido"], "fido", "mfa")).Result);
        var denied = await SignInAsync(["fido"], "pwd", "mfa");
        Assert.Null(denied.Result);
        Assert.Equal("not_phishing_resistant", denied.Error);
        Assert.Equal("pwd,mfa", denied.Amr);
    }

    [Fact]
    public async Task Accepted_methods_can_be_a_comma_list_from_one_variable()
    {
        Assert.NotNull((await SignInAsync(["fido, ngcmfa"], "ngcmfa", "mfa")).Result);
        Assert.NotNull((await SignInAsync([""], "fido")).Result); // leer = Standard fido
        Assert.Null((await SignInAsync([""], "rsa", "mfa")).Result);
    }
}
