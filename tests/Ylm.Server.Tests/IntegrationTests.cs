using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ylm.Agent.Core;
using Ylm.Contracts;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Integrations;

namespace Ylm.Server.Tests;

/// <summary>V2: ToDo-Liste über die API, Webhooks, Mail-Berichte, NinjaOne.</summary>
public sealed class IntegrationTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private FakeGraph _graph = null!;
    private HttpClient _owner = null!;
    private Guid _tenantId;

    public async Task InitializeAsync()
    {
        NinjaService.ForgetToken();
        _graph = new FakeGraph();
        _graph.Users.Add(new GraphUser("u-anna", "anna@contoso.com", "Anna Beispiel", "anna@contoso.com", "Vertrieb", true));
        _graph.Users.Add(new GraphUser("u-ben", "ben@contoso.com", "Ben Muster", "ben@contoso.com", "IT", true));
        _app.Graph.Tenants[_graph.TenantId] = _graph;
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var s = await db.Installation.FirstAsync();
            s.OrganizationName = "Muster IT";
            s.ClientId = Guid.NewGuid();
            s.HomeTenantId = Guid.NewGuid();
            var t = new Tenant { EntraTenantId = _graph.TenantId, DisplayName = "Contoso GmbH", DefaultDomain = "contoso.com" };
            db.Tenants.Add(t);
            db.Profiles.Add(new RegistrationProfile { Name = "Standard", IsDefault = true });
            await db.SaveChangesAsync();
            _tenantId = t.Id;
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
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<HttpClient> ApiClientAsync(bool todoWrite)
    {
        var created = await Json(await _owner.PostAsJsonAsync("/api/api-keys", new { name = todoWrite ? "Ticketsystem" : "Nur lesen", allTenants = true, allowTodoWrite = todoWrite }));
        var c = _app.Client();
        c.DefaultRequestHeaders.Add("X-Api-Key", created.GetProperty("apiKey").GetString());
        return c;
    }

    private async Task RunWebhooksAsync() => await _app.Services.GetRequiredService<IntegrationJobs>().RunWebhooksAsync(CancellationToken.None);

    [Fact]
    public async Task Ticket_system_pushes_todos_and_registration_completes_them()
    {
        var readOnly = await ApiClientAsync(false);
        var denied = await readOnly.PostAsJsonAsync("/api/v1/todos", new { tenant = "contoso.com", userPrincipalName = "anna@contoso.com" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var ticket = await ApiClientAsync(true);
        var noTenant = await ticket.PostAsJsonAsync("/api/v1/todos", new { userPrincipalName = "anna@contoso.com" });
        Assert.Equal(HttpStatusCode.OK, (await ticket.GetAsync("/api/v1/todos")).StatusCode);
        // Mit einem Tenant gilt ein Schlüssel für alle Tenants auch ohne Angabe; mit zweien braucht es die Angabe.
        Assert.Equal(HttpStatusCode.Created, noTenant.StatusCode);
        var unknown = await ticket.PostAsJsonAsync("/api/v1/todos", new { tenant = "contoso.com", userPrincipalName = "niemand@contoso.com" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);

        var created = await ticket.PostAsJsonAsync("/api/v1/todos", new { tenant = "contoso.com", userPrincipalName = "ben@contoso.com", ticketId = "INC-4711", ticketUrl = "https://tickets.example/INC-4711", note = "Neuer Mitarbeiter", profile = "Standard" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var todo = await Json(created);
        Assert.Equal("Open", todo.GetProperty("status").GetString());
        Assert.Equal("Standard", todo.GetProperty("profile").GetString());
        Assert.StartsWith("API: Ticketsystem", todo.GetProperty("source").GetString());
        var duplicate = await ticket.PostAsJsonAsync("/api/v1/todos", new { tenant = "contoso.com", userPrincipalName = "ben@contoso.com", ticketId = "INC-4712" });
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(todo.GetProperty("id").GetString(), (await Json(duplicate)).GetProperty("id").GetString());

        // In der Oberfläche
        var list = await Json(await _owner.GetAsync($"/api/todos?tenantId={_tenantId}"));
        Assert.Equal(2, list.GetArrayLength());

        // Registrierung für Ben erledigt sein ToDo automatisch.
        var device = new SimulatedKeyDevice { SerialNumber = 50_000_001 };
        var code = (await Json(await _owner.PostAsync("/api/agents/pairing-codes", null))).GetProperty("code").GetString()!;
        await using var agent = _app.CreateAgent(device);
        Assert.True((await agent.PairAsync("https://ylm.test", code, CancellationToken.None)).Ok);
        for (var i = 0; i < 100 && agent.Status.Connection != BackendConnectionState.Connected; i++) await Task.Delay(100);
        var agentId = (await Json(await _owner.GetAsync("/api/agents")))[0].GetProperty("id").GetGuid();
        var profileId = (await Json(await _owner.GetAsync($"/api/profiles?tenantId={_tenantId}")))[0].GetProperty("id").GetGuid();
        var job = await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/registrations", new { entraUserId = "u-ben", profileId, agentId, performReset = false }));
        for (var i = 0; i < 100 && (await Json(await _owner.GetAsync($"/api/jobs/{job.GetProperty("id")}"))).GetProperty("state").GetString() == "Running"; i++) await Task.Delay(100);
        var done = await Json(await ticket.GetAsync($"/api/v1/todos/{todo.GetProperty("id").GetString()}"));
        Assert.Equal("Done", done.GetProperty("status").GetString());
        Assert.Equal("50000001", done.GetProperty("keySerialNumber").GetString());
        var byTicket = await Json(await ticket.GetAsync("/api/v1/todos?ticketId=INC-4711"));
        Assert.Single(byTicket.EnumerateArray());

        // Annas ToDo zieht das Ticketsystem zurück.
        var annaTodo = (await Json(await _owner.GetAsync($"/api/todos?tenantId={_tenantId}")))[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.DeleteAsync($"/api/v1/todos/{annaTodo}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await ticket.DeleteAsync($"/api/v1/todos/{annaTodo}?reason=Storniert")).StatusCode);
        Assert.Empty((await Json(await _owner.GetAsync($"/api/todos?tenantId={_tenantId}"))).EnumerateArray());

        // Die übrigen Endpunkte der API bleiben nur lesend.
        var openapi = await (await _app.Client().GetAsync("/openapi/v1.json")).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(openapi);
        foreach (var path in doc.RootElement.GetProperty("paths").EnumerateObject())
            foreach (var op in path.Value.EnumerateObject().Where(o => o.Name is "post" or "put" or "patch" or "delete"))
                Assert.StartsWith("/api/v1/todos", path.Name);
    }

    [Fact]
    public async Task Todos_become_a_batch()
    {
        await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/todos", new { userPrincipalName = "anna@contoso.com", ticketId = "T-1" }));
        await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/todos", new { entraUserId = "u-ben" }));
        var todos = await Json(await _owner.GetAsync($"/api/todos?tenantId={_tenantId}"));
        var profileId = (await Json(await _owner.GetAsync($"/api/profiles?tenantId={_tenantId}")))[0].GetProperty("id").GetGuid();
        var batch = await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/todos/batch",
            new { todoIds = todos.EnumerateArray().Select(t => t.GetProperty("id").GetGuid()), defaultProfileId = profileId }));
        var loaded = await Json(await _owner.GetAsync($"/api/batches/{batch.GetProperty("id").GetGuid()}"));
        Assert.Equal(2, loaded.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Webhooks_deliver_signed_events_and_retry()
    {
        var created = await Json(await _owner.PostAsJsonAsync("/api/integrations/webhooks",
            new { name = "Ticketsystem", url = "https://hooks.example/ylm", events = new[] { "todo.created", "todo.cancelled" }, allTenants = true }));
        var secret = created.GetProperty("secret").GetString()!;
        var hookId = created.GetProperty("webhook").GetProperty("id").GetGuid();
        Assert.StartsWith("whsec_", secret);

        // Test-Knopf
        var test = await Json(await _owner.PostAsync($"/api/integrations/webhooks/{hookId}/test", null));
        Assert.True(test.GetProperty("success").GetBoolean());
        Assert.True(_app.Http.Calls.TryDequeue(out var testCall));
        Assert.Equal("webhook.test", testCall.Headers["X-YLM-Event"]);

        await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/todos", new { userPrincipalName = "anna@contoso.com", ticketId = "T-9" }));
        await Json(await _owner.PostAsJsonAsync($"/api/tenants/{_tenantId}/approvals", new { entraUserIds = new[] { "u-ben" }, custodian = "Tresor", reason = "x" })); // nicht abonniert
        await RunWebhooksAsync();
        Assert.True(_app.Http.Calls.TryDequeue(out var call));
        Assert.Empty(_app.Http.Calls);
        Assert.Equal("todo.created", call.Headers["X-YLM-Event"]);
        Assert.Equal(WebhookService.Sign(secret, call.Headers["X-YLM-Timestamp"], call.Body), call.Headers["X-YLM-Signature"]);
        using (var payload = JsonDocument.Parse(call.Body))
        {
            Assert.Equal("anna@contoso.com", payload.RootElement.GetProperty("target").GetString());
            Assert.Equal("Contoso GmbH", payload.RootElement.GetProperty("tenant").GetProperty("name").GetString());
            Assert.Equal("T-9", payload.RootElement.GetProperty("details").GetProperty("ticketId").GetString());
        }

        // Fehler: Wiederholung später, nichts doppelt.
        _app.Http.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var todoId = (await Json(await _owner.GetAsync($"/api/todos?tenantId={_tenantId}")))[0].GetProperty("id").GetGuid();
        await Json(await _owner.PostAsJsonAsync($"/api/todos/{todoId}/cancel", new { reason = "doppelt" }));
        await RunWebhooksAsync();
        await RunWebhooksAsync();
        Assert.Single(_app.Http.Calls);
        var deliveries = await Json(await _owner.GetAsync($"/api/integrations/webhooks/{hookId}/deliveries"));
        var failed = deliveries.EnumerateArray().First(d => d.GetProperty("eventType").GetString() == "todo.cancelled");
        Assert.Equal("Pending", failed.GetProperty("status").GetString());
        Assert.Equal(503, failed.GetProperty("lastStatusCode").GetInt32());

        _app.Http.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.NoContent);
        var retry = await Json(await _owner.PostAsync($"/api/integrations/webhooks/deliveries/{failed.GetProperty("id").GetGuid()}/retry", null));
        Assert.True(retry.GetProperty("success").GetBoolean());

        // Ungültige URL
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PostAsJsonAsync("/api/integrations/webhooks", new { name = "x", url = "ftp://x", allTenants = true })).StatusCode);
        var op = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/integrations/webhooks")).StatusCode);
    }

    [Fact]
    public async Task Mail_reports_are_scheduled_and_sent_with_pdf()
    {
        var schedule = new { name = "Wochenbericht", recipients = new[] { "it@contoso.com; chef@contoso.com" }, allTenants = true, frequency = "Weekly", dayOfWeek = 1, dayOfMonth = 1, hour = 7, timeZone = "Europe/Berlin", includeCsv = true };
        var notConfigured = await Json(await _owner.PostAsJsonAsync("/api/integrations/reports", schedule));
        var id = notConfigured.GetProperty("id").GetGuid();
        Assert.Equal(2, notConfigured.GetProperty("recipients").GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await _owner.PostAsync($"/api/integrations/reports/{id}/send", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.PutAsJsonAsync("/api/integrations/smtp",
            new { host = "smtp.contoso.com", port = 587, security = "StartTls", username = "ylm", password = "geheim", fromAddress = "ylm@contoso.com", fromName = "YubiKeys" })).StatusCode);
        var smtp = await Json(await _owner.GetAsync("/api/integrations/smtp"));
        Assert.True(smtp.GetProperty("hasPassword").GetBoolean());
        Assert.False(smtp.TryGetProperty("password", out _));

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.PostAsJsonAsync("/api/integrations/smtp/test", new { to = "it@contoso.com" })).StatusCode);
        Assert.True(_app.Mail.Sent.TryDequeue(out var testMail));
        Assert.Equal("geheim", testMail.Password);

        await Json(await _owner.PostAsync($"/api/integrations/reports/{id}/send", null));
        Assert.True(_app.Mail.Sent.TryDequeue(out var report));
        Assert.Equal(["it@contoso.com", "chef@contoso.com"], report.Mail.To);
        Assert.Contains(report.Mail.Attachments, a => a.ContentType == "application/pdf" && a.Content.Length > 1000);
        Assert.Contains(report.Mail.Attachments, a => a.ContentType == "text/csv");
        Assert.Contains("Wochenbericht", report.Mail.Subject);

        // Fällige Berichte gehen im Hintergrund raus; Fehler werden am Bericht angezeigt.
        await _app.WithDbAsync(async db =>
        {
            var s = await db.ReportSchedules.FindAsync(id);
            s!.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        });
        _app.Mail.Fail = new InvalidOperationException("Relay denied");
        await _app.Services.GetRequiredService<IntegrationJobs>().RunScheduledAsync(CancellationToken.None);
        var after = (await Json(await _owner.GetAsync("/api/integrations/reports")))[0];
        Assert.False(after.GetProperty("lastSucceeded").GetBoolean());
        Assert.Contains("Relay denied", after.GetProperty("lastError").GetString());
        Assert.True(after.GetProperty("nextRunAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Next_run_respects_time_zone_and_frequency()
    {
        var weekly = new ReportSchedule { Frequency = ReportFrequency.Weekly, DayOfWeek = 1, Hour = 7, TimeZone = "Europe/Berlin" };
        // Donnerstag, 19.11.2026 12:00 UTC → Montag, 23.11.2026 07:00 Berlin = 06:00 UTC
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 6, 0, 0, TimeSpan.Zero), ReportMailService_NextRun(weekly, new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero)));
        var daily = new ReportSchedule { Frequency = ReportFrequency.Daily, Hour = 7, TimeZone = "Europe/Berlin" };
        // Sommerzeit: 07:00 Berlin = 05:00 UTC
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 5, 0, 0, TimeSpan.Zero), ReportMailService_NextRun(daily, new DateTimeOffset(2026, 7, 1, 6, 0, 0, TimeSpan.Zero)));
        var monthly = new ReportSchedule { Frequency = ReportFrequency.Monthly, DayOfMonth = 1, Hour = 8, TimeZone = "UTC" };
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero), ReportMailService_NextRun(monthly, new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero)));
    }

    private static DateTimeOffset ReportMailService_NextRun(ReportSchedule s, DateTimeOffset after) => ReportMailService.NextRun(s, after);

    [Fact]
    public async Task Ninja_pushes_metrics_into_organization_fields()
    {
        _app.Http.Respond = (req, body) => req.RequestUri!.AbsolutePath switch
        {
            "/ws/oauth/token" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token = "tok-1", expires_in = 3600 }) },
            "/v2/organizations" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new { id = 7L, name = "Contoso" }, new { id = 8L, name = "Fabrikam" } }) },
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        };
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.PutAsJsonAsync("/api/integrations/ninja", new
        {
            enabled = true, baseUrl = "https://eu.ninjarmm.com", clientId = "cid", clientSecret = "csecret", intervalMinutes = 60,
            fieldMap = new Dictionary<string, string> { ["summary"] = "ylmSummary", ["keysActive"] = "ylmKeysActive", ["coveragePercent"] = "" },
        })).StatusCode);
        var orgs = await Json(await _owner.GetAsync("/api/integrations/ninja/organizations"));
        Assert.Equal(2, orgs.GetArrayLength());
        Assert.True(_app.Http.Calls.TryDequeue(out var tokenCall));
        Assert.Contains("client_secret=csecret", tokenCall.Body);
        Assert.True(_app.Http.Calls.TryDequeue(out var orgCall));
        Assert.Equal("Bearer tok-1", orgCall.Headers["Authorization"]);

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.PutAsJsonAsync($"/api/integrations/ninja/tenants/{_tenantId}", new { organizationId = 7, organizationName = "Contoso" })).StatusCode);
        await _app.WithDbAsync(async db =>
        {
            db.Keys.Add(new YubiKey { TenantId = _tenantId, SerialNumber = 1, Status = KeyStatus.InStock, Source = KeySource.Import });
            await db.SaveChangesAsync();
        });
        var result = await Json(await _owner.PostAsync("/api/integrations/ninja/push", null));
        Assert.Equal(1, result.GetProperty("succeeded").GetInt32());
        var patch = _app.Http.Calls.Last();
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.Equal("/v2/organization/7/custom-fields", patch.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(patch.Body);
        Assert.Equal(0, body.RootElement.GetProperty("ylmKeysActive").GetInt32());
        Assert.Contains("1 im Lager", body.RootElement.GetProperty("ylmSummary").GetString());
        Assert.False(body.RootElement.TryGetProperty("ylmCoveragePercent", out _));

        var settings = await Json(await _owner.GetAsync("/api/integrations/ninja"));
        Assert.True(settings.GetProperty("lastRunSucceeded").GetBoolean());
        Assert.False(settings.TryGetProperty("clientSecret", out _));

        // Falsche Zugangsdaten werden verständlich gemeldet.
        NinjaService.ForgetToken();
        _app.Http.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var failed = await Json(await _owner.PostAsync("/api/integrations/ninja/push", null));
        Assert.Contains("Anmeldung bei NinjaOne fehlgeschlagen", failed.GetProperty("errors")[0].GetString());
    }
}
