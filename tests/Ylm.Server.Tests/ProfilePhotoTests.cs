using System.Net;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;

namespace Ylm.Server.Tests;

public sealed class ProfilePhotoTests : IDisposable
{
    private readonly TestApp _app = new();
    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Entra_user_gets_the_photo_from_the_home_tenant_others_get_404()
    {
        var home = Guid.NewGuid();
        var oid = Guid.NewGuid();
        var graph = new FakeGraph();
        graph.Photos[oid.ToString()] = [0xFF, 0xD8, 0xFF, 0xE0];
        _app.Graph.Tenants[home] = graph;

        var timo = await _app.LoginAsync("timo@home.example", PlatformRole.Owner);
        // Ohne Heim-Tenant und Objekt-ID gibt es kein Bild: Initialen.
        Assert.Equal(HttpStatusCode.NotFound, (await timo.GetAsync("/api/me/photo")).StatusCode);

        await _app.WithDbAsync(async db =>
        {
            (await db.Installation.FirstAsync()).HomeTenantId = home;
            var u = await db.PlatformUsers.FirstAsync(x => x.Upn == "timo@home.example");
            u.EntraObjectId = oid;
            u.LoginKind = UserLoginKind.Entra;
            db.PlatformUsers.Add(new PlatformUser { Upn = "ohne-bild@home.example", DisplayName = "Ohne", Role = PlatformRole.Auditor, EntraObjectId = Guid.NewGuid() });
            await db.SaveChangesAsync();
        });
        var res = await timo.GetAsync("/api/me/photo");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/jpeg", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal(4, (await res.Content.ReadAsByteArrayAsync()).Length);

        var other = await _app.LoginAsync("ohne-bild@home.example");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync("/api/me/photo")).StatusCode);
    }
}
