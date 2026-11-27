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

    private static object Layout(params object[] blocks) => new { version = 1, blocks };

    [Fact]
    public async Task Letter_layout_is_saved_per_level_and_previewed()
    {
        var info = await Json(await _owner.GetAsync("/api/branding/letter"));
        Assert.Contains(info.GetProperty("types").EnumerateArray(), t => t.GetProperty("type").GetString() == "pin");
        Assert.Contains(info.GetProperty("variables").EnumerateArray(), v => v.GetProperty("name").GetString() == "Vorname");
        Assert.Equal(JsonValueKind.Null, info.GetProperty("layout").ValueKind);
        Assert.Contains(info.GetProperty("inherited").GetProperty("blocks").EnumerateArray(), b => b.GetProperty("type").GetString() == "pinChange");

        // Partner: unbekannte Eigenschaften fallen weg, halbe Breite nur, wo erlaubt.
        var saved = await Json(await _owner.PutAsJsonAsync("/api/branding/letter", new
        {
            layout = Layout(
                new { id = "t", type = "heading", props = new Dictionary<string, string> { ["text"] = "Willkommen, {{Vorname}}", ["unbekannt"] = "x" } },
                new { id = "p", type = "pin", width = "half" },
                new { id = "c", type = "callout", width = "half", show = "forced", props = new Dictionary<string, string> { ["tone"] = "warn" } },
                new { id = "s", type = "steps", width = "half" }),
        }));
        var blocks = saved.GetProperty("layout").GetProperty("blocks");
        Assert.Equal(4, blocks.GetArrayLength());
        Assert.Equal("Willkommen, {{Vorname}}", blocks[0].GetProperty("props").GetProperty("text").GetString());
        Assert.False(blocks[0].GetProperty("props").TryGetProperty("unbekannt", out _));
        Assert.Equal("half", blocks[1].GetProperty("width").GetString());
        Assert.Equal("forced", blocks[2].GetProperty("show").GetString());
        Assert.Equal("full", blocks[3].GetProperty("width").GetString());

        // Tenant erbt den Aufbau des Partners, bis er einen eigenen speichert; null setzt zurück.
        var t = await Json(await _owner.GetAsync($"/api/branding/letter?tenantId={_tenant}"));
        Assert.Equal(JsonValueKind.Null, t.GetProperty("layout").ValueKind);
        Assert.Equal(4, t.GetProperty("inherited").GetProperty("blocks").GetArrayLength());
        await Json(await _owner.PutAsJsonAsync($"/api/branding/letter?tenantId={_tenant}", new { layout = Layout(new { type = "pin" }) }));
        t = await Json(await _owner.GetAsync($"/api/branding/letter?tenantId={_tenant}"));
        Assert.Equal(1, t.GetProperty("layout").GetProperty("blocks").GetArrayLength());
        await Json(await _owner.PutAsJsonAsync($"/api/branding/letter?tenantId={_tenant}", new { layout = (object?)null }));
        t = await Json(await _owner.GetAsync($"/api/branding/letter?tenantId={_tenant}"));
        Assert.Equal(JsonValueKind.Null, t.GetProperty("layout").ValueKind);

        // Ungültige Aufbauten
        foreach (var bad in new[]
        {
            Layout(new { type = "heading" }),
            Layout(new { type = "pin" }, new { type = "pin" }),
            Layout(new { type = "pin" }, new { type = "unbekannt" }),
            Layout(new { type = "pin", show = "own" }),
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PutAsJsonAsync("/api/branding/letter", new { layout = bad })).StatusCode);

        var res = await _owner.PostAsJsonAsync($"/api/branding/letter/preview.pdf?tenantId={_tenant}", new { layout = (object?)null, pinChangeForced = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("%PDF", Encoding.ASCII.GetString((await res.Content.ReadAsByteArrayAsync())[..4]));

        var auditor = await _app.LoginAsync("auditor@home.example", PlatformRole.Auditor);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PutAsJsonAsync("/api/branding/letter", new { layout = Layout(new { type = "pin" }) })).StatusCode);
    }

    [Fact]
    public async Task Earlier_letter_texts_become_blocks()
    {
        await _app.WithDbAsync(async db =>
        {
            db.Brandings.Add(new Branding { LetterTexts = """{"title":"Alter Titel","closing":"-"}""" });
            db.Brandings.Add(new Branding { TenantId = _tenant, LetterTexts = """{"hints":"Nur ein Hinweis"}""" });
            await db.SaveChangesAsync();
        });
        var p = await Json(await _owner.GetAsync("/api/branding/letter"));
        var blocks = p.GetProperty("layout").GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal("Alter Titel", blocks.First(b => b.GetProperty("type").GetString() == "heading").GetProperty("props").GetProperty("text").GetString());
        Assert.DoesNotContain(blocks, b => b.GetProperty("id").GetString() == "gruss");

        var t = await Json(await _owner.GetAsync($"/api/branding/letter?tenantId={_tenant}"));
        var tb = t.GetProperty("layout").GetProperty("blocks").EnumerateArray().ToList();
        Assert.Equal("Nur ein Hinweis", tb.First(b => b.GetProperty("type").GetString() == "hints").GetProperty("props").GetProperty("items").GetString());
        Assert.Equal("Alter Titel", tb.First(b => b.GetProperty("type").GetString() == "heading").GetProperty("props").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Long_letters_continue_on_a_second_page()
    {
        var blocks = new List<object> { new { type = "pin" } };
        for (var i = 0; i < 12; i++) blocks.Add(new { type = "steps" });
        var res = await _owner.PostAsJsonAsync("/api/branding/letter/preview.pdf", new { layout = Layout([.. blocks]), pinChangeForced = true });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(await res.Content.ReadAsByteArrayAsync()), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.True(doc.PageCount >= 3);
    }

    [Fact]
    public async Task Page_breaks_start_new_pages_and_empty_pages_are_skipped()
    {
        async Task<int> Pages(bool forced, params object[] blocks)
        {
            var res = await _owner.PostAsJsonAsync("/api/branding/letter/preview.pdf", new { layout = Layout(blocks), pinChangeForced = forced });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(await res.Content.ReadAsByteArrayAsync()), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            return doc.PageCount;
        }
        Assert.Equal(2, await Pages(true, new { type = "pin" }, new { type = "pageBreak" }, new { type = "text" }));
        Assert.Equal(3, await Pages(true, new { type = "heading" }, new { type = "pageBreak" }, new { type = "pin" }, new { type = "pageBreak" }, new { type = "text" }));
        // Umbruch am Anfang, doppelter Umbruch und leere letzte Seite erzeugen keine leeren Seiten.
        Assert.Equal(2, await Pages(true, new { type = "pageBreak" }, new { type = "pin" }, new { type = "pageBreak" }, new { type = "pageBreak" }, new { type = "text" }, new { type = "pageBreak" }));
        // Seite 2 enthält nur einen Baustein für die dauerhafte PIN: im Brief mit Initial-PIN entfällt sie.
        Assert.Equal(1, await Pages(true, new { type = "pin" }, new { type = "pageBreak" }, new { type = "text", show = "own" }));
        Assert.Equal(2, await Pages(false, new { type = "pin" }, new { type = "pageBreak" }, new { type = "text", show = "own" }));

        // Gespeichert wird der Umbruch wie jeder Baustein; mehr als 6 Seiten werden abgelehnt.
        await Json(await _owner.PutAsJsonAsync("/api/branding/letter", new { layout = Layout(new { type = "pin" }, new { type = "pageBreak" }, new { type = "text" }) }));
        var p = await Json(await _owner.GetAsync("/api/branding/letter"));
        Assert.Contains(p.GetProperty("layout").GetProperty("blocks").EnumerateArray(), b => b.GetProperty("type").GetString() == "pageBreak");
        var tooMany = new List<object> { new { type = "pin" } };
        for (var i = 0; i < 6; i++) tooMany.Add(new { type = "pageBreak" });
        var bad = await _owner.PutAsJsonAsync("/api/branding/letter", new { layout = Layout([.. tooMany]) });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        await Json(await _owner.PutAsJsonAsync("/api/branding/letter", new { layout = (object?)null }));
    }

    /// <summary>Legt Muster-PIN-Briefe ab, wenn YLM_SAMPLE_DIR gesetzt ist (CI lädt sie als Artefakt hoch, zum Ansehen im PR).</summary>
    [Fact]
    public async Task Sample_letters_render()
    {
        var dir = Environment.GetEnvironmentVariable("YLM_SAMPLE_DIR");
        // Mit Firmenname, Logo und Kontakt: so wird der Brief am längsten und muss trotzdem auf eine Seite passen.
        await Json(await _owner.PutAsJsonAsync("/api/branding", new { companyName = "Muster IT GmbH", contact = "IT-Service · 0800 123 456" }));
        await Json(await _owner.PutAsync("/api/branding/logo", new ByteArrayContent(Png)));
        foreach (var forced in new[] { false, true })
        {
            var res = await _owner.PostAsJsonAsync("/api/branding/letter/preview.pdf", new { layout = (object?)null, pinChangeForced = forced });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var pdf = await res.Content.ReadAsByteArrayAsync();
            Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf[..4]));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(Path.Combine(dir, forced ? "PIN-Brief-PIN-Wechsel-erzwungen.pdf" : "PIN-Brief-PIN-selbst-aendern.pdf"), pdf);
            }
            using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(pdf), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
                Assert.Equal(1, doc.PageCount); // der Standardbrief passt auf eine Seite
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
