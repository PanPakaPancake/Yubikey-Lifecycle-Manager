using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;

namespace Ylm.Server.Tests;

/// <summary>Tenant-Onboarding durch Admins, die nur für einzelne Tenants zuständig sind.</summary>
public sealed class TenantOnboardingScopeTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private FakeGraph _graph = null!;
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        _graph = new FakeGraph();
        _app.Graph.Tenants[_graph.TenantId] = _graph;
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var s = await db.Installation.FirstAsync();
            s.OrganizationName = "Muster IT";
            s.ClientId = Guid.NewGuid();
            s.HomeTenantId = Guid.NewGuid();
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

    private async Task<HttpClient> LimitedAdminAsync(string upn, Guid roleId)
    {
        await Json(await _owner.PostAsJsonAsync("/api/users", new
        {
            upn, displayName = upn.Split('@')[0], role = "Custom", customRoleId = roleId, allTenants = false, tenantIds = Array.Empty<Guid>(),
        }));
        return await _app.LoginAsync(upn);
    }

    private async Task<HttpResponseMessage> ConsentAsync(HttpClient client)
    {
        var consent = await Json(await client.PostAsJsonAsync("/api/tenants/consent", new { tenantHint = (string?)null }));
        var query = HttpUtility.ParseQueryString(new Uri(consent.GetProperty("url").GetString()!).Query);
        return await client.GetAsync($"/api/tenants/consent-callback?tenant={_graph.TenantId}&admin_consent=True&state={Uri.EscapeDataString(query["state"]!)}");
    }

    [Fact]
    public async Task Onboarded_tenant_is_assigned_to_a_limited_admin_and_others_cannot_take_it_over()
    {
        var role = await Json(await _owner.PostAsJsonAsync("/api/roles", new { name = "Onboarding", permissions = new[] { "ReadInventory", "ManageTenants" } }));
        var roleId = role.GetProperty("id").GetGuid();

        var first = await LimitedAdminAsync("anna@home.example", roleId);
        var callback = await ConsentAsync(first);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var location = callback.Headers.Location!.ToString();
        Assert.StartsWith("/tenants/", location);
        var tenantId = Guid.Parse(location.Split('/')[2].Split('?')[0]);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync($"/api/tenants/{tenantId}")).StatusCode);

        var second = await LimitedAdminAsync("ben@home.example", roleId);
        var denied = await ConsentAsync(second);
        Assert.Contains("consentError", denied.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await second.GetAsync($"/api/tenants/{tenantId}")).StatusCode);
    }
}
