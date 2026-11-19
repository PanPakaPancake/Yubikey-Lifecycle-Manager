using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Ylm.Core;
using Ylm.Server.Data;

namespace Ylm.Server.Tests;

/// <summary>Übersicht „Wer hat welchen Key“ samt Exporten.</summary>
public sealed class AssignmentTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private HttpClient _owner = null!;
    private Guid _tenantA;
    private Guid _tenantB;

    public async Task InitializeAsync()
    {
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var a = new Tenant { EntraTenantId = Guid.NewGuid(), DisplayName = "Kunde A", Status = TenantStatus.Active };
            var b = new Tenant { EntraTenantId = Guid.NewGuid(), DisplayName = "Kunde B", Status = TenantStatus.Active };
            db.Tenants.AddRange(a, b);
            _tenantA = a.Id;
            _tenantB = b.Id;

            YubiKey Key(Tenant t, int? serial, KeyStatus status, KeyUsage usage = KeyUsage.Personal, string? custodian = null) =>
                new() { TenantId = t.Id, SerialNumber = serial, Status = status, Usage = usage, Custodian = custodian, Model = "YubiKey 5 NFC", FirmwareVersion = "5.7.1", Source = KeySource.Platform };
            KeyRegistration Reg(Tenant t, string upn, string name) =>
                new() { TenantId = t.Id, EntraUserId = upn, UserPrincipalName = upn, UserDisplayName = name, EntraMethodId = Guid.NewGuid().ToString(), RegisteredAt = DateTimeOffset.UtcNow.AddDays(-3) };

            var anna = Key(a, 1001, KeyStatus.Active);
            anna.Registrations.Add(Reg(a, "anna@a.example", "Anna Alt"));
            var annaSpare = Key(a, 1004, KeyStatus.Active);
            annaSpare.Registrations.Add(Reg(a, "anna@a.example", "Anna Alt"));
            var shared = Key(a, 1002, KeyStatus.Active, KeyUsage.Shared, "Bernd Boss");
            shared.Registrations.Add(Reg(a, "prod1@a.example", "Produktion 1"));
            shared.Registrations.Add(Reg(a, "prod2@a.example", "Produktion 2"));
            var stock = Key(a, 1003, KeyStatus.InStock);
            var gone = Key(a, 1005, KeyStatus.Active);
            gone.Registrations.Add(new KeyRegistration { TenantId = a.Id, EntraUserId = "x", UserPrincipalName = "x@a.example", EntraMethodId = "m", RemovedAt = DateTimeOffset.UtcNow });
            var carl = Key(b, null, KeyStatus.Active);
            carl.Registrations.Add(Reg(b, "carl@b.example", "Carl Cee"));
            db.Keys.AddRange(anna, annaSpare, shared, stock, gone, carl);
            await db.SaveChangesAsync();
        });
    }

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

    [Fact]
    public async Task Overview_lists_who_has_which_key()
    {
        var all = await Json(await _owner.GetAsync("/api/assignments"));
        var keys = all.GetProperty("keys").EnumerateArray().ToList();
        // Lagerkey 1003 fehlt, alle anderen ausgegebenen Keys sind drin.
        Assert.Equal(5, keys.Count);
        Assert.DoesNotContain(keys, k => k.GetProperty("serialNumber").ValueKind == JsonValueKind.Number && k.GetProperty("serialNumber").GetInt32() == 1003);

        var shared = keys.Single(k => k.GetProperty("usage").GetString() == "Shared");
        Assert.Equal("Bernd Boss", shared.GetProperty("holder").GetString());
        Assert.Equal(2, shared.GetProperty("accounts").GetArrayLength());

        var gone = keys.Single(k => k.GetProperty("serialNumber").ValueKind == JsonValueKind.Number && k.GetProperty("serialNumber").GetInt32() == 1005);
        Assert.Contains("Kein Konto mehr auf dem Key registriert", gone.GetProperty("issues").EnumerateArray().Select(i => i.GetString()));

        var summary = all.GetProperty("summary");
        Assert.Equal(5, summary.GetProperty("keys").GetInt32());
        Assert.Equal(4, summary.GetProperty("people").GetInt32()); // Anna, Prod 1, Prod 2, Carl
        Assert.Equal(1, summary.GetProperty("peopleWithSeveralKeys").GetInt32());
        Assert.Equal(1, summary.GetProperty("withoutSerial").GetInt32());

        var onlyB = await Json(await _owner.GetAsync($"/api/assignments?tenantId={_tenantB}"));
        Assert.Equal("Carl Cee", onlyB.GetProperty("keys")[0].GetProperty("holder").GetString());

        // Suche findet auch Konten auf geteilten Keys.
        var search = await Json(await _owner.GetAsync("/api/assignments?q=prod2"));
        Assert.Equal(1002, search.GetProperty("keys").EnumerateArray().Single().GetProperty("serialNumber").GetInt32());
        var usage = await Json(await _owner.GetAsync("/api/assignments?usage=Shared"));
        Assert.Single(usage.GetProperty("keys").EnumerateArray());
    }

    [Fact]
    public async Task Exports_contain_one_row_per_person()
    {
        var csv = await _owner.GetAsync($"/api/exports/assignments.csv?tenantId={_tenantA}");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var text = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
        var lines = text.Trim('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Person;UPN;Seriennummer", lines[0]);
        // Anna (2 Keys), Prod 1, Prod 2, Key ohne Konto.
        Assert.Equal(5, lines.Length - 1);
        Assert.Contains(lines, l => l.Contains("prod2@a.example") && l.Contains("Bernd Boss"));

        var xlsx = await _owner.GetAsync("/api/exports/assignments.xlsx");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        using (var zip = new ZipArchive(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync())))
        {
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var sheet = reader.ReadToEnd();
            Assert.Contains("carl@b.example", sheet);
            Assert.Contains("<autoFilter ref=\"A1:", sheet);
        }

        var pdf = await _owner.GetAsync("/api/exports/assignments.pdf?usage=Shared");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));

        Assert.Equal(HttpStatusCode.NotFound, (await _owner.GetAsync("/api/exports/assignments.doc")).StatusCode);
    }

    [Fact]
    public async Task Tenant_scoped_user_sees_only_own_tenant()
    {
        await _app.WithDbAsync(async db =>
        {
            db.PlatformUsers.Add(new PlatformUser { Upn = "helpdesk@home.example", DisplayName = "HD", Role = PlatformRole.Helpdesk, AllTenants = false, TenantAssignments = [new PlatformUserTenant { TenantId = _tenantB }] });
            await db.SaveChangesAsync();
        });
        var hd = await _app.LoginAsync("helpdesk@home.example");
        var res = await Json(await hd.GetAsync("/api/assignments"));
        Assert.All(res.GetProperty("keys").EnumerateArray(), k => Assert.Equal("Kunde B", k.GetProperty("tenantName").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await hd.GetAsync($"/api/assignments?tenantId={_tenantA}")).StatusCode);
    }
}
