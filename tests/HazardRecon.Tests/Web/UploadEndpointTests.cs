using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HazardRecon.Web.Files;
using HazardRecon.Web.Runs;
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
        /// <summary>Shared so a test can inspect what the endpoint recorded.</summary>
        public FakeRunStore RunStore { get; } = new();

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

                // in-memory stores: this suite is about the upload path, not
                // about reaching a real Supabase project
                services.RemoveAll<IRunStore>();
                services.RemoveAll<IRunFileStore>();
                services.RemoveAll<IFileStore>();
                services.AddSingleton<IRunStore>(RunStore);
                services.AddSingleton<IRunFileStore>(new FakeRunFileStore());
                services.AddSingleton<IFileStore>(new FakeFileStore());
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
    public async Task TestTheRunIsRecordedAgainstTheCaller()
    {
        HttpClient client = _factory.CreateClient();
        int before = _factory.RunStore.Runs.Count;

        using MultipartFormDataContent form = new();
        AddFile(form, "set0", "MAR 2026/lgd_defaults.csv", "account\nC1\n");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // the row is what makes the run survive a restart; without it the
        // database stays empty however many runs are started
        Assert.Equal(before + 1, _factory.RunStore.Runs.Count);

        RunRecord run = _factory.RunStore.Runs[^1];
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), run.UserId);
        Assert.Contains("MAR 2026", run.SetLabels);
    }

    [Fact]
    public async Task TestTheRunIdIsTheDatabaseId()
    {
        // artifact paths and every later lookup key off this, so a locally
        // invented id would orphan the row
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFile(form, "set0", "APR 2026/lgd_defaults.csv", "account\nD1\n");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains(_factory.RunStore.Runs[^1].Id.ToString(), body);
    }

    [Fact]
    public async Task TestTheDailyRunQuotaIsEnforced()
    {
        HttpClient client = _factory.CreateClient();
        _factory.RunStore.RecentCount = 20;

        try
        {
            using MultipartFormDataContent form = new();
            AddFile(form, "set0", "MAY 2026/lgd_defaults.csv", "account\nE1\n");

            HttpResponseMessage response = await client.PostAsync("/api/discover", form);
            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Contains("limit is 20", body);
        }
        finally
        {
            _factory.RunStore.RecentCount = 0;
        }
    }

    [Fact]
    public async Task TestAnotherUsersRunIsReportedMissingNotForbidden()
    {
        // a 403 would confirm the run exists; 404 tells an outsider nothing
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TestHistoryListsOnlyTheCallersRuns()
    {
        HttpClient client = _factory.CreateClient();

        // a run belonging to somebody else must never appear
        await _factory.RunStore.CreateAsync(Guid.NewGuid(), new[] { "SOMEONE ELSE" });

        HttpResponseMessage response = await client.GetAsync("/api/runs");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("SOMEONE ELSE", body);
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
