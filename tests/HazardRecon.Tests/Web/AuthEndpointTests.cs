using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>
/// Boots the real app and asserts the authorization boundary. Every request here
/// is deliberately anonymous, so the JWT handler never fetches a JWKS and the
/// tests stay offline.
/// </summary>
public class AuthEndpointTests : IClassFixture<AuthEndpointTests.Factory>
{
    public class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Supabase:Url"] = "https://ref.supabase.co",
                    ["Supabase:AnonKey"] = "anon-key-for-tests",
                    ["Supabase:ServiceRoleKey"] = "service-key-for-tests"
                }));

            return base.CreateHost(builder);
        }
    }

    private readonly Factory _factory;

    public AuthEndpointTests(Factory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/models")]
    [InlineData("/api/job/anything")]
    public async Task TestProtectedEndpointsRejectAnAnonymousCaller(string path)
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestHealthStaysOpen()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TestConfigServesTheAnonKeyButNeverTheServiceRoleKey()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/config");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("anon-key-for-tests", body);
        Assert.DoesNotContain("service-key-for-tests", body);
    }
}
