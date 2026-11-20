using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Security;

namespace Ylm.Server.Tests;

/// <summary>Notfallcodes: einmalige Anmeldung für Owner, gehasht gespeichert, im Audit-Log sichtbar.</summary>
public sealed class RecoveryCodeTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private HttpClient _owner = null!;

    public async Task InitializeAsync() => _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<string[]> CreateCodesAsync() =>
        (await Json(await _owner.PostAsync("/api/me/recovery-codes", null))).GetProperty("codes").EnumerateArray().Select(c => c.GetString()!).ToArray();

    [Fact]
    public async Task Owner_gets_codes_that_work_once_and_are_stored_hashed()
    {
        Assert.Equal(0, (await Json(await _owner.GetAsync("/api/me"))).GetProperty("recoveryCodes").GetInt32());
        var codes = await CreateCodesAsync();
        Assert.Equal(RecoveryCodeService.CodeCount, codes.Length);
        Assert.All(codes, c => Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", c));
        Assert.Equal(codes.Length, codes.Distinct().Count());
        await _app.WithDbAsync(async db =>
        {
            var stored = await db.RecoveryCodes.Select(c => c.CodeHash).ToListAsync();
            Assert.DoesNotContain(stored, h => codes.Any(c => h.Contains(c.Replace("-", ""))));
        });
        var methods = await Json(await _app.Client().GetAsync("/auth/methods"));
        Assert.True(methods.GetProperty("recovery").GetBoolean());

        // Kleinschreibung und Leerzeichen sind egal.
        var anon = _app.Client();
        var login = await Json(await anon.PostAsJsonAsync("/auth/recovery", new { code = " " + codes[0].ToLowerInvariant().Replace("-", " ") }));
        Assert.Equal(codes.Length - 1, login.GetProperty("remaining").GetInt32());
        var me = await Json(await anon.GetAsync("/api/me"));
        Assert.Equal("owner@home.example", me.GetProperty("user").GetProperty("upn").GetString());
        Assert.Equal(codes.Length - 1, me.GetProperty("recoveryCodes").GetInt32());

        var again = await _app.Client().PostAsJsonAsync("/auth/recovery", new { code = codes[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Contains("invalid_recovery_code", await again.Content.ReadAsStringAsync());

        await _app.WithDbAsync(async db =>
        {
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.RecoveryCodeUsed));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.LoginDenied));
        });
    }

    [Fact]
    public async Task New_codes_replace_the_old_ones()
    {
        var first = await CreateCodesAsync();
        var second = await CreateCodesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().PostAsJsonAsync("/auth/recovery", new { code = first[1] })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _app.Client().PostAsJsonAsync("/auth/recovery", new { code = second[1] })).StatusCode);
        var status = await Json(await _owner.GetAsync("/api/me/recovery-codes"));
        Assert.Equal(RecoveryCodeService.CodeCount - 1, status.GetProperty("remaining").GetInt32());
    }

    [Fact]
    public async Task Only_active_owners_can_create_or_use_codes()
    {
        var op = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsync("/api/me/recovery-codes", null)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(await op.GetAsync("/api/me"))).GetProperty("recoveryCodes").ValueKind);

        var codes = await CreateCodesAsync();
        await _app.WithDbAsync(async db =>
        {
            var owner = await db.PlatformUsers.FirstAsync(u => u.Upn == "owner@home.example");
            owner.Role = PlatformRole.Operator;
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().PostAsJsonAsync("/auth/recovery", new { code = codes[0] })).StatusCode);
    }

    [Fact]
    public void Codes_have_80_bits_and_normalize()
    {
        Assert.Equal("ABCD2345EFGH6789", RecoveryCodeService.Normalize(" abcd-2345 efgh_6789 "));
        Assert.Equal(RecoveryCodeService.HashOf("ABCD-2345-EFGH-6789"), RecoveryCodeService.HashOf("abcd2345efgh6789"));
    }
}
