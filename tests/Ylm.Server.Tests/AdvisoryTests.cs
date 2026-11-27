using System.Net;
using System.Text;
using System.Text.Json;
using Ylm.Core;
using Ylm.Server.Data;

namespace Ylm.Server.Tests;

/// <summary>Übersicht der Yubico-Sicherheitshinweise, Inventarfilter und Export.</summary>
public sealed class AdvisoryTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private HttpClient _owner = null!;
    private Guid _tenantA;

    public async Task InitializeAsync()
    {
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var a = new Tenant { EntraTenantId = Guid.NewGuid(), DisplayName = "Kunde A", Status = TenantStatus.Active };
            db.Tenants.Add(a);
            _tenantA = a.Id;
            YubiKey Key(Guid? tenant, int serial, string model, string? fw, KeyStatus status = KeyStatus.Active) =>
                new() { TenantId = tenant, SerialNumber = serial, Model = model, FirmwareVersion = fw, Status = status, Source = KeySource.Platform };
            var old = Key(a.Id, 2001, "YubiKey 5 NFC", "5.4.3");
            old.Registrations.Add(new KeyRegistration { TenantId = a.Id, EntraUserId = "u1", UserPrincipalName = "anna@a.example", UserDisplayName = "Anna Alt", EntraMethodId = "m1" });
            var bio = Key(a.Id, 2002, "YubiKey Bio", "5.7.1");
            bio.Registrations.Add(new KeyRegistration { TenantId = a.Id, EntraUserId = "u2", UserPrincipalName = "bert@a.example", UserDisplayName = "Bert Bio", EntraMethodId = "m2" });
            var current = Key(a.Id, 2003, "YubiKey 5C NFC", "5.7.4");
            var retired = Key(a.Id, 2004, "YubiKey 5 NFC", "5.2.7", KeyStatus.Retired);
            var noFirmware = Key(a.Id, 2005, "YubiKey 5 NFC", null);
            var stock = Key(null, 2006, "YubiKey 5 NFC", "5.4.3", KeyStatus.InStock);
            db.Keys.AddRange(old, bio, current, retired, noFirmware, stock);
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

    private static int[] Serials(JsonElement items) => items.EnumerateArray().Select(k => k.GetProperty("serialNumber").GetInt32()).Order().ToArray();

    [Fact]
    public async Task Overview_counts_keys_and_people_per_advisory()
    {
        var o = await Json(await _owner.GetAsync("/api/advisories"));
        // Ausgemustert zählt nicht; ohne Tenant-Auswahl ist das Partnerlager dabei.
        Assert.Equal(4, o.GetProperty("keysChecked").GetInt32());
        Assert.Equal(1, o.GetProperty("keysWithoutFirmware").GetInt32());
        Assert.Equal(3, o.GetProperty("affectedKeys").GetInt32());
        Assert.Equal(2, o.GetProperty("affectedPeople").GetInt32());

        var list = o.GetProperty("advisories").EnumerateArray().ToDictionary(a => a.GetProperty("id").GetString()!);
        Assert.Equal(FirmwareAdvisories.Known.Count, list.Count);
        var eucleak = list["YSA-2024-03"];
        Assert.Equal([2001, 2002, 2006], Serials(eucleak.GetProperty("items")));
        Assert.Equal(2, eucleak.GetProperty("people").GetInt32());
        Assert.Equal(1, eucleak.GetProperty("byStatus").GetProperty("InStock").GetInt32());
        Assert.Equal("Partnerlager", eucleak.GetProperty("items").EnumerateArray().Single(k => k.GetProperty("serialNumber").GetInt32() == 2006).GetProperty("tenantName").GetString());
        // RP-Auflisten: beim Bio erst 5.6.4 behoben, 5.7.1 also nicht betroffen.
        Assert.Equal([2001, 2006], Serials(list["YSA-2024-02"].GetProperty("items")));
        Assert.Equal(0, list["YSA-2020-05"].GetProperty("keys").GetInt32());

        var onlyA = await Json(await _owner.GetAsync($"/api/advisories?tenantId={_tenantA}"));
        Assert.Equal(2, onlyA.GetProperty("affectedKeys").GetInt32());
    }

    [Fact]
    public async Task Inventory_filters_by_advisory()
    {
        // Das Inventar filtert unabhängig vom Status, der ausgemusterte 5.2.7 (2004) ist also dabei.
        var fido = await Json(await _owner.GetAsync("/api/keys?advisory=fido"));
        Assert.Equal([2001, 2002, 2004, 2006], Serials(fido.GetProperty("items")));
        var one = await Json(await _owner.GetAsync($"/api/keys?advisory=YSA-2024-02&tenantId={_tenantA}&status=Active"));
        Assert.Equal([2001], Serials(one.GetProperty("items")));
        Assert.Equal(1, one.GetProperty("total").GetInt32());
        var any = await Json(await _owner.GetAsync("/api/keys?advisory=any&status=Retired"));
        Assert.Equal([2004], Serials(any.GetProperty("items")));
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.GetAsync("/api/keys?advisory=YSA-1999-99")).StatusCode);

        var dash = await Json(await _owner.GetAsync($"/api/dashboard?tenantId={_tenantA}"));
        Assert.Equal(2, dash.GetProperty("vulnerableKeys").GetInt32());
        Assert.Contains(dash.GetProperty("advisories").EnumerateArray(), a => a.GetProperty("id").GetString() == "YSA-2025-02" && a.GetProperty("keys").GetInt32() == 2);
    }

    [Fact]
    public async Task Export_lists_one_row_per_advisory_and_key()
    {
        var csv = await _owner.GetAsync($"/api/exports/advisories.csv?tenantId={_tenantA}&advisory=YSA-2024-03");
        var text = Encoding.UTF8.GetString(await csv.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        var lines = text.Trim('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Hinweis;Titel;Einstufung;Betrifft Passkeys;Seriennummer", lines[0]);
        Assert.Equal(2, lines.Length - 1);
        Assert.Contains(lines, l => l.Contains("Anna Alt (anna@a.example)"));

        var xlsx = await _owner.GetAsync("/api/exports/advisories.xlsx");
        Assert.Equal(HttpStatusCode.OK, xlsx.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.GetAsync("/api/exports/advisories.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.GetAsync("/api/exports/advisories.csv?advisory=YSA-1999-99")).StatusCode);
    }
}
