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
/// Drives POST /api/discover with a real multipart body tagging each file by
/// set index and role (set{N}.{kind}), the contract SetFileReceiver expects.
/// </summary>
public class UploadEndpointTests : IClassFixture<UploadEndpointTests.AuthedFactory>
{
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
        public FakeRunStore RunStore { get; } = new();
        public FakeColumnMappingStore MappingStore { get; } = new();

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

                services.RemoveAll<IRunStore>();
                services.RemoveAll<IRunFileStore>();
                services.RemoveAll<IFileStore>();
                services.RemoveAll<IColumnMappingStore>();
                services.AddSingleton<IRunStore>(RunStore);
                services.AddSingleton<IRunFileStore>(new FakeRunFileStore());
                services.AddSingleton<IFileStore>(new FakeFileStore());
                services.AddSingleton<IColumnMappingStore>(MappingStore);
            });

            return base.CreateHost(builder);
        }
    }

    private readonly AuthedFactory _factory;

    public UploadEndpointTests(AuthedFactory factory) => _factory = factory;

    private static void AddFile(MultipartFormDataContent form, int setIndex, string kind, string fileName, string body)
    {
        ByteArrayContent content = new(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, $"set{setIndex}.{kind}", fileName);
    }

    private static void AddFullSet(MultipartFormDataContent form, int setIndex, string exposureName = "IFRS9 FILE.csv")
    {
        AddFile(form, setIndex, "exposure", exposureName, "A1,2026-06-30,100,Stage 2\n");
        AddFile(form, setIndex, "writeoff", "WRITEOFF.csv", "LoanAccountNumber,CustomerId,Amount,ReportDate\nA1,C1,100,2026-04-30\n");
        // deliberately not a real zip: these tests only care about upload/run
        // bookkeeping, not full discovery - InputDiscoverer.BuildSet fails to
        // extract it and reports the set as having no analysis data, which is
        // fine here (see TestTheResponseIncludesMappingDataForBothCsvFiles for
        // the one test that needs discovery to actually succeed).
        AddFile(form, setIndex, "debug", "debug.zip", "zipbytes");
        AddFile(form, setIndex, "scenario", "scenario.json", "{}");
    }

    /// <summary>Loose (not zipped) debug files so InputDiscoverer.BuildSet actually finds lgd_defaults.csv.</summary>
    private static void AddDiscoverableSet(MultipartFormDataContent form, int setIndex, string exposureName = "IFRS9 FILE.csv")
    {
        AddFile(form, setIndex, "exposure", exposureName, "A1,2026-06-30,100,Stage 2\n");
        AddFile(form, setIndex, "writeoff", "WRITEOFF.csv", "LoanAccountNumber,CustomerId,Amount,ReportDate\nA1,C1,100,2026-04-30\n");
        AddFile(form, setIndex, "debug", "lgd_defaults.csv", "AccountNumber,EventType,CohortDate,Bucket,Rating,Amount\nA1,Lifetime,2026-05-31,0,5,100.0\n");
        AddFile(form, setIndex, "debug", "pd_scored.csv", "AccountNumber,Category1,ReportDate,BucketRating,NextBucketRating,DeltaLambda\nA1,Loans,2026-01-31,1,2,0.1\n");
        AddFile(form, setIndex, "debug", "debug.json", "{}");
        AddFile(form, setIndex, "scenario", "scenario.json", "{}");
    }

    [Fact]
    public async Task TestAnUploadedSetIsRecordedAgainstTheCaller()
    {
        HttpClient client = _factory.CreateClient();
        int before = _factory.RunStore.Runs.Count;

        using MultipartFormDataContent form = new();
        AddFullSet(form, 0, "MAR 2026.csv");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("run_id", body);
        Assert.Equal(before + 1, _factory.RunStore.Runs.Count);

        RunRecord run = _factory.RunStore.Runs[^1];
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), run.UserId);
        Assert.Contains("MAR 2026", run.SetLabels);
    }

    [Fact]
    public async Task TestTheResponseIncludesMappingDataForBothCsvFiles()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddDiscoverableSet(form, 0);

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"writeoff\"", body);
        Assert.Contains("\"exposure\"", body);
        // the write-off file's real headers were matched by name, no AI guess needed
        Assert.Contains("header_match", body);
    }

    [Fact]
    public async Task TestTwoSetsBecomeTwoInventoryEntries()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFullSet(form, 0, "JAN 2026.csv");
        AddFullSet(form, 1, "FEB 2026.csv");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("JAN 2026", body);
        Assert.Contains("FEB 2026", body);
    }

    [Fact]
    public async Task TestAMissingRequiredFileIsRejected()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFile(form, 0, "writeoff", "WRITEOFF.csv", "a,b\n1,2\n");
        AddFile(form, 0, "debug", "debug.zip", "zipbytes");
        AddFile(form, 0, "scenario", "scenario.json", "{}");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("exposure", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAnUnknownFieldNameIsRejected()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFile(form, 0, "notakind", "x.csv", "data");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TestTheRunIdIsTheDatabaseId()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFullSet(form, 0, "APR 2026.csv");

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
            AddFullSet(form, 0, "MAY 2026.csv");

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
    public async Task TestAnEmptyUploadIsRejected()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at least one", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAnotherUsersRunIsReportedMissingNotForbidden()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TestHistoryListsOnlyTheCallersRuns()
    {
        HttpClient client = _factory.CreateClient();

        await _factory.RunStore.CreateAsync(Guid.NewGuid(), new[] { "SOMEONE ELSE" });

        HttpResponseMessage response = await client.GetAsync("/api/runs");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("SOMEONE ELSE", body);
    }
}
