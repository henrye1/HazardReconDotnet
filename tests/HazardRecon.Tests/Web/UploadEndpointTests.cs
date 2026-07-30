using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>
/// Drives POST /api/discover with a real multipart body.
///
/// The unit tests cover the receiver in isolation; what only a real request can
/// show is whether the folder structure survives the wire at all - the relative
/// path rides in the Content-Disposition filename, and a framework that stripped
/// the directory part would quietly flatten every upload.
/// </summary>
public class UploadEndpointTests : IClassFixture<UploadEndpointTests.AuthedFactory>
{
    /// <summary>Accepts every request, so the upload path is what is under test.</summary>
    private class AlwaysOnHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public AlwaysOnHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims = { new("sub", "11111111-1111-1111-1111-111111111111") };
            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, "Test")));
        }
    }

    public class AuthedFactory : WebApplicationFactory<Program>
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

            builder.ConfigureServices(services =>
            {
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, AlwaysOnHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                    o.DefaultScheme = "Test";
                });
            });

            return base.CreateHost(builder);
        }
    }

    private readonly AuthedFactory _factory;

    public UploadEndpointTests(AuthedFactory factory) => _factory = factory;

    private static void AddFile(MultipartFormDataContent form, string field, string relativePath, string body)
    {
        ByteArrayContent content = new(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // the relative path rides here, exactly as the browser sends it
        form.Add(content, field, relativePath);
    }

    [Fact]
    public async Task TestAnUploadedFolderIsDiscoveredAsASet()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFile(form, "set0", "DEBUG FILE 30 JUNE 2026 0.5 PERCENT/lgd_defaults.csv",
            "account,exposure\nA1,100\n");
        AddFile(form, "set0", "DEBUG FILE 30 JUNE 2026 0.5 PERCENT/write-off.csv",
            "account,amount\nA1,100\n");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // the folder name survived the round trip, so the discoverer could derive
        // a set from it - a flattened upload would have found nothing
        Assert.Contains("run_id", body);
        Assert.Contains("JUN2026 0.5PCT", body);
        Assert.Contains("lgd_defaults.csv", body);
    }

    [Fact]
    public async Task TestTwoFoldersBecomeTwoSets()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFile(form, "set0", "JAN 2026/lgd_defaults.csv", "account\nA1\n");
        AddFile(form, "set1", "FEB 2026/lgd_defaults.csv", "account\nB1\n");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("JAN2026", body);
        Assert.Contains("FEB2026", body);
    }

    [Fact]
    public async Task TestATraversingFilenameIsRejected()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFile(form, "set0", "../../escaped.csv", "pwned");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unsafe", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAnUnknownFieldNameIsRejected()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFile(form, "notaset", "x/y.csv", "data");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TestAnEmptyUploadIsRejected()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at least one", body, StringComparison.OrdinalIgnoreCase);
    }
}
