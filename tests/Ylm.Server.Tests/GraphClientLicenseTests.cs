using System.Net;
using System.Text;
using Ylm.Server.Graph;

namespace Ylm.Server.Tests;

public sealed class GraphClientLicenseTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Without_premium_license_users_are_checked_individually()
    {
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.Contains("/reports/"))
                return Json(HttpStatusCode.Forbidden, """{"error":{"code":"Authentication_RequestFromNonPremiumTenantOrB2CTenant","message":"Tenant is not a B2C tenant and doesn't have premium license"}}""");
            if (path.EndsWith("/v1.0/users"))
                return Json(HttpStatusCode.OK, """{"value":[{"id":"u1"},{"id":"u2"}]}""");
            if (path.Contains("/users/u1/"))
                return Json(HttpStatusCode.OK, """{"value":[{"id":"m1"}]}""");
            return Json(HttpStatusCode.OK, """{"value":[]}""");
        });
        var graph = new GraphClient(new HttpClient(handler), _ => Task.FromResult("x.e30.x"), "https://graph.test");

        var ids = new List<string>();
        await foreach (var id in graph.ListUserIdsWithPasskeysAsync()) ids.Add(id);

        Assert.Equal(new[] { "u1" }, ids);
        Assert.Equal(1, handler.Requests.Count(r => r.Contains("/reports/")));
    }

    [Fact]
    public async Task With_report_access_the_report_is_used()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"value":[{"id":"u1","methodsRegistered":["passKeyDeviceBound"]},{"id":"u2","methodsRegistered":["password"]}]}"""));
        var graph = new GraphClient(new HttpClient(handler), _ => Task.FromResult("x.e30.x"), "https://graph.test");

        var ids = new List<string>();
        await foreach (var id in graph.ListUserIdsWithPasskeysAsync()) ids.Add(id);

        Assert.Equal(new[] { "u1" }, ids);
        Assert.Single(handler.Requests);
    }
}

public sealed class GraphClientRolloutTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Without_premium_registration_details_come_from_each_user()
    {
        var handler = new StubHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var query = req.RequestUri.Query;
            if (path.Contains("/reports/") || query.Contains("signInActivity"))
                return Json(HttpStatusCode.Forbidden, """{"error":{"code":"Authentication_RequestFromNonPremiumTenantOrB2CTenant","message":"Tenant is not a B2C tenant and doesn't have premium license"}}""");
            if (path.EndsWith("/v1.0/users"))
                return Json(HttpStatusCode.OK, """{"value":[{"id":"u1","userPrincipalName":"a@x","accountEnabled":true,"userType":"Member"},{"id":"u2","userPrincipalName":"b@x"}]}""");
            if (path.EndsWith("/users/u1/authentication/methods"))
                return Json(HttpStatusCode.OK, """{"value":[{"@odata.type":"#microsoft.graph.passwordAuthenticationMethod","id":"pw"},{"@odata.type":"#microsoft.graph.phoneAuthenticationMethod","id":"3179e48a","phoneNumber":"+49 1","phoneType":"office"}]}""");
            return Json(HttpStatusCode.OK, """{"value":[{"@odata.type":"#microsoft.graph.fido2AuthenticationMethod","id":"f","model":"YubiKey 5"}]}""");
        });
        var graph = new GraphClient(new HttpClient(handler), _ => Task.FromResult("x.e30.x"), "https://graph.test");

        var result = await graph.ListUserRegistrationDetailsAsync();

        Assert.False(result.FromReport);
        Assert.Equal(["password", "officePhone"], result.Users.Single(u => u.Id == "u1").MethodsRegistered);
        Assert.Equal(["passKeyDeviceBound"], result.Users.Single(u => u.Id == "u2").MethodsRegistered);
    }

    [Fact]
    public async Task Deleting_uses_the_path_of_the_method_type()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var graph = new GraphClient(new HttpClient(handler), _ => Task.FromResult("x.e30.x"), "https://graph.test");
        await graph.DeleteAuthenticationMethodAsync("u1", "microsoftAuthenticator", "m1");
        Assert.Equal("DELETE /v1.0/users/u1/authentication/microsoftAuthenticatorMethods/m1", handler.Requests.Single());
        await Assert.ThrowsAsync<ArgumentException>(() => graph.DeleteAuthenticationMethodAsync("u1", "password", "pw"));
    }
}
