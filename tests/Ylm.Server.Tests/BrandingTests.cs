using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ylm.Core;
using Ylm.Server.Data;

namespace Ylm.Server.Tests;

/// <summary>Whitelabeling der Dokumente: Partner-Vorgabe, Überschreiben pro Tenant, Logo.</summary>
public sealed class BrandingTests : IAsyncLifetime
{
    // 1×1 Pixel, PNG.
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly TestApp _app = new();
    private HttpClient _owner = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var t = new Tenant { EntraTenantId = Guid.NewGuid(), DisplayName = "Kunde A", Status = TenantStatus.Active };
            db.Tenants.Add(t);
            await db.SaveChangesAsync();
            _tenant = t.Id;
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
    public async Task Letter_texts_are_saved_per_level_and_previewed()
    {
        var info = await Json(await _owner.GetAsync("/api/branding/letter"));
        Assert.Contains(info.GetProperty("fields").EnumerateArray(), f => f.GetProperty("key").GetString() == "title");
        Assert.Contains(info.GetProperty("variables").EnumerateArray(), v => v.GetProperty("name").GetString() == "Vorname");

        // Partner: unbekannte Felder und unveränderte Standardtexte werden nicht gespeichert.
        var saved = await Json(await _owner.PutAsJsonAsync("/api/branding/letter", new
        {
            texts = new Dictionary<string, string?> { ["title"] = "Willkommen, {{Vorname}}", ["unbekannt"] = "x", ["guideTitle"] = Pdf.LetterTemplate.Default("guideTitle"), ["intro"] = "  " },
        }));
        var partnerTexts = saved.GetProperty("texts");
        Assert.Equal("Willkommen, {{Vorname}}", partnerTexts.GetProperty("title").GetString());
        Assert.False(partnerTexts.TryGetProperty("unbekannt", out _));
        Assert.False(partnerTexts.TryGetProperty("guideTitle", out _));
        Assert.False(partnerTexts.TryGetProperty("intro", out _));

        await Json(await _owner.PutAsJsonAsync($"/api/branding/letter?tenantId={_tenant}", new { texts = new Dictionary<string, string?> { ["hints"] = "Nur ein Hinweis" } }));
        var t = await Json(await _owner.GetAsync($"/api/branding/letter?tenantId={_tenant}"));
        Assert.Equal("Nur ein Hinweis", t.GetProperty("tenant").GetProperty("hints").GetString());
        Assert.Equal("Willkommen, {{Vorname}}", t.GetProperty("partner").GetProperty("title").GetString());

        var res = await _owner.PostAsJsonAsync($"/api/branding/letter/preview.pdf?tenantId={_tenant}", new { texts = new Dictionary<string, string?> { ["closing"] = "-" }, pinChangeForced = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("%PDF", Encoding.ASCII.GetString((await res.Content.ReadAsByteArrayAsync())[..4]));

        var auditor = await _app.LoginAsync("auditor@home.example", PlatformRole.Auditor);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PutAsJsonAsync("/api/branding/letter", new { texts = new Dictionary<string, string?>() })).StatusCode);
    }

    /// <summary>Legt Muster-PIN-Briefe ab, wenn YLM_SAMPLE_DIR gesetzt ist (CI lädt sie als Artefakt hoch, zum Ansehen im PR).</summary>
    [Fact]
    public async Task Sample_letters_render()
    {
        var dir = Environment.GetEnvironmentVariable("YLM_SAMPLE_DIR");
        foreach (var forced in new[] { true, false })
        {
            var res = await _owner.PostAsJsonAsync("/api/branding/letter/preview.pdf", new { texts = new Dictionary<string, string?>(), pinChangeForced = forced });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var pdf = await res.Content.ReadAsByteArrayAsync();
            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf[..4]));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(Path.Combine(dir, forced ? "PIN-Brief-PIN-Wechsel-erzwungen.pdf" : "PIN-Brief-PIN-selbst-aendern.pdf"), pdf);
            }
        }
    }

    [Fact]
    public void Letter_placeholders_and_layers()
    {
        var values = new Dictionary<string, string?> { ["Vorname"] = "Erika", ["Kontakt"] = null };
        Assert.Equal("Hallo Erika, {{Unbekannt}} bleibt.", Pdf.LetterTemplate.Render("Hallo {{ Vorname }}, {{Unbekannt}} bleibt.", values));
        Assert.Equal("IT: ", Pdf.LetterTemplate.Render("IT: {{Kontakt}}", values));
        var tenant = new Dictionary<string, string> { ["title"] = "Tenant" };
        var partner = new Dictionary<string, string> { ["title"] = "Partner", ["closing"] = "Gruß" };
        Assert.Equal("Tenant", Pdf.LetterTemplate.Resolve("title", tenant, partner));
        Assert.Equal("Gruß", Pdf.LetterTemplate.Resolve("closing", tenant, partner));
        Assert.Equal(Pdf.LetterTemplate.Default("hints"), Pdf.LetterTemplate.Resolve("hints", tenant, partner));
        Assert.True(Pdf.LetterTemplate.IsOmitted(" - "));
    }

    [Fact]
    public async Task Tenant_overrides_single_fields_and_inherits_the_rest()
    {
        await Json(await _owner.PutAsJsonAsync("/api/branding", new { companyName = "Partner IT", accentColor = "#0A7A50", contact = "Hotline 0800 123", footer = "Partner IT GmbH" }));
        await Json(await _owner.PutAsJsonAsync($"/api/branding?tenantId={_tenant}", new { companyName = "Kunde A AG", accentColor = (string?)null, contact = (string?)null, footer = (string?)null }));

        var b = await Json(await _owner.GetAsync($"/api/branding?tenantId={_tenant}"));
        var eff = b.GetProperty("effective");
        Assert.Equal("Kunde A AG", eff.GetProperty("companyName").GetString());
        Assert.Equal("#0a7a50", eff.GetProperty("accentColor").GetString());
        Assert.Equal("Hotline 0800 123", eff.GetProperty("contact").GetString());
        Assert.False(eff.GetProperty("hasLogo").GetBoolean());

        var bad = await _owner.PutAsJsonAsync("/api/branding", new { accentColor = "grün" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Logo_upload_is_validated_and_inherited()
    {
        var notImage = await _owner.PutAsync("/api/branding/logo", new ByteArrayContent(Encoding.UTF8.GetBytes("<svg/>")));
        Assert.Equal(HttpStatusCode.BadRequest, notImage.StatusCode);

        var ok = await Json(await _owner.PutAsync("/api/branding/logo", new ByteArrayContent(Png)));
        Assert.True(ok.GetProperty("hasLogo").GetBoolean());

        // Tenant ohne eigenes Logo bekommt das Partner-Logo.
        var logo = await _owner.GetAsync($"/api/branding/logo?tenantId={_tenant}&effective=true");
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/png", logo.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.GetAsync($"/api/branding/logo?tenantId={_tenant}")).StatusCode);

        var pdf = await _owner.GetAsync($"/api/branding/preview.pdf?tenantId={_tenant}");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        var bytes = await pdf.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));

        await Json(await _owner.DeleteAsync("/api/branding/logo"));
        Assert.Equal(HttpStatusCode.NotFound, (await _owner.GetAsync($"/api/branding/logo?tenantId={_tenant}&effective=true")).StatusCode);
    }

    [Fact]
    public async Task Only_admins_may_change_branding()
    {
        await _app.WithDbAsync(async db =>
        {
            db.PlatformUsers.Add(new PlatformUser { Upn = "hd@home.example", DisplayName = "HD", Role = PlatformRole.Helpdesk });
            await db.SaveChangesAsync();
        });
        var hd = await _app.LoginAsync("hd@home.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await hd.PutAsJsonAsync("/api/branding", new { companyName = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await hd.PutAsJsonAsync($"/api/branding?tenantId={_tenant}", new { companyName = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await hd.GetAsync("/api/branding")).StatusCode);
    }

    [Fact]
    public async Task Public_branding_for_passkey_page_works_without_login()
    {
        await Json(await _owner.PutAsJsonAsync("/api/branding", new { companyName = "Partner IT", accentColor = "#0A7A50", contact = "Hotline 0800 123", footer = "intern" }));
        await Json(await _owner.PutAsJsonAsync($"/api/branding?tenantId={_tenant}", new { companyName = "Kunde A AG", accentColor = (string?)null, contact = (string?)null, footer = (string?)null }));

        var anon = _app.Client();
        var partner = await Json(await anon.GetAsync("/api/public/branding"));
        Assert.Equal("Partner IT", partner.GetProperty("companyName").GetString());
        Assert.Equal("#0a7a50", partner.GetProperty("accentColor").GetString());
        Assert.Equal("Hotline 0800 123", partner.GetProperty("contact").GetString());
        Assert.False(partner.TryGetProperty("footer", out _));

        var tenant = await Json(await anon.GetAsync($"/api/public/branding?t={_tenant}"));
        Assert.Equal("Kunde A AG", tenant.GetProperty("companyName").GetString());
        // Unbekannte Tenant-Id: Partner-Vorgabe.
        var unknown = await Json(await anon.GetAsync($"/api/public/branding?t={Guid.NewGuid()}"));
        Assert.Equal("Partner IT", unknown.GetProperty("companyName").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/public/branding/logo")).StatusCode);
        // Die Verwaltung bleibt geschützt.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/branding")).StatusCode);

        var pdf = await _owner.GetAsync($"/api/branding/preview.pdf?tenantId={_tenant}");
        var text = Encoding.Latin1.GetString(await pdf.Content.ReadAsByteArrayAsync());
        Assert.Contains($"/passkey?t={_tenant}", text);
    }
}
