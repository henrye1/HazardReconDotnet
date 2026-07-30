using System.Net;
using HazardRecon.Web.Supabase;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>
/// The browser cannot put an Authorization header on an iframe load, a link
/// navigation or a download, so file requests authenticate with a cookie
/// instead. These pin how that token is chosen and how tightly the cookie is
/// scoped.
/// </summary>
public class DownloadCookieTests : IClassFixture<AuthEndpointTests.Factory>
{
    private readonly AuthEndpointTests.Factory _factory;

    public DownloadCookieTests(AuthEndpointTests.Factory factory) => _factory = factory;

    [Fact]
    public void TestTheHeaderWinsWhenBothArePresent()
    {
        Assert.Equal("from-header", SupabaseJwt.TokenForRequest("from-header", "from-cookie"));
    }

    [Fact]
    public void TestTheCookieIsUsedWhenThereIsNoHeader()
    {
        Assert.Equal("from-cookie", SupabaseJwt.TokenForRequest(null, "from-cookie"));
        Assert.Equal("from-cookie", SupabaseJwt.TokenForRequest("", "from-cookie"));
    }

    [Fact]
    public void TestNeitherYieldsNoToken()
    {
        Assert.Null(SupabaseJwt.TokenForRequest(null, null));
        Assert.Null(SupabaseJwt.TokenForRequest("", ""));
    }

    [Fact]
    public async Task TestTheFileRouteStillRejectsAnAnonymousCaller()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/runs/anything/output/dashboard.html");

        // no header, no cookie - the regression this whole mechanism exists to
        // fix must not have reopened the route to everyone
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestSettingTheCookieRequiresAToken()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsync("/api/session", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestSignOutClearsTheCookieAndScopesItToRuns()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.DeleteAsync("/api/session");
        string setCookie = string.Join(";", response.Headers.GetValues("Set-Cookie"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(SupabaseJwt.DownloadCookie, setCookie);
        // scoped to /runs so the token is never sent to the JSON API
        Assert.Contains("path=/runs", setCookie, StringComparison.OrdinalIgnoreCase);
    }
}
