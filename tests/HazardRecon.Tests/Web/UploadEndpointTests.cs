using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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

    /// <summary>The same, minus the write-off file the receiver no longer insists on.</summary>
    private static void AddDiscoverableSetWithoutWriteOff(
        MultipartFormDataContent form, int setIndex, string exposureName = "IFRS9 FILE.csv")
    {
        AddFile(form, setIndex, "exposure", exposureName, "A1,2026-06-30,100,Stage 2\n");
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

    [Fact]
    public async Task TestConfirmingAMappingSavesItForReuse()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent discoverForm = new();
        AddDiscoverableSet(discoverForm, 0, "JUN2026.csv");
        HttpResponseMessage discoverResponse = await client.PostAsync("/api/discover", discoverForm);
        string discoverBody = await discoverResponse.Content.ReadAsStringAsync();

        using JsonDocument doc = JsonDocument.Parse(discoverBody);
        string runId = doc.RootElement.GetProperty("run_id").GetString()!;
        string setKey = doc.RootElement.GetProperty("mapping")[0].GetProperty("key").GetString()!;

        var mappingBody = new
        {
            run_id = runId,
            sets = new[]
            {
                new
                {
                    key = setKey,
                    writeoff = new Dictionary<string, string>
                    {
                        ["LoanAccountNumber"] = "LoanAccountNumber", ["CustomerId"] = "CustomerId",
                        ["Amount"] = "Amount", ["ReportDate"] = "ReportDate"
                    },
                    exposure = new Dictionary<string, string> { ["LoanAccountNumber"] = "0", ["AmountOutstanding"] = "2" }
                }
            }
        };

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/discover/mapping", mappingBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(_factory.MappingStore.RunMappings);
        Assert.Contains(_factory.MappingStore.RunMappings, m => m.FileKind == "writeoff" && m.Mapping["Amount"] == "Amount");
        Assert.Contains(_factory.MappingStore.RunMappings, m => m.FileKind == "exposure" && m.Mapping["LoanAccountNumber"] == "0");
        // the saved profile is also updated, so a future upload with this column shape reuses it
        Assert.NotEmpty(_factory.MappingStore.Saved);
    }

    [Fact]
    public async Task TestASetWithNoWriteOffFileDiscoversAndSaysWhatItCosts()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddDiscoverableSetWithoutWriteOff(form, 0, "NOWO2026.csv");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument doc = JsonDocument.Parse(body);

        // the mapping step is told there is nothing to map for the write-off,
        // rather than being handed a half-built view of a file that is not there
        JsonElement mapping = doc.RootElement.GetProperty("mapping")[0];
        Assert.Equal(JsonValueKind.Null, mapping.GetProperty("writeoff").ValueKind);
        Assert.Equal(JsonValueKind.Object, mapping.GetProperty("exposure").ValueKind);

        // and the run is warned about the consequence, which is the whole reason
        // the upload is allowed through
        string problems = doc.RootElement.GetProperty("problems").ToString();
        Assert.Contains("check 2", problems, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestConfirmingAMappingForASetWithNoWriteOffRecordsOnlyTheExposure()
    {
        HttpClient client = _factory.CreateClient();
        int before = _factory.MappingStore.RunMappings.Count;

        using MultipartFormDataContent discoverForm = new();
        AddDiscoverableSetWithoutWriteOff(discoverForm, 0, "NOWOMAP2026.csv");
        HttpResponseMessage discoverResponse = await client.PostAsync("/api/discover", discoverForm);
        using JsonDocument doc = JsonDocument.Parse(await discoverResponse.Content.ReadAsStringAsync());
        string runId = doc.RootElement.GetProperty("run_id").GetString()!;
        string setKey = doc.RootElement.GetProperty("mapping")[0].GetProperty("key").GetString()!;

        // the client sends no writeoff object, because it was given no table for one
        var mappingBody = new
        {
            run_id = runId,
            sets = new[]
            {
                new
                {
                    key = setKey,
                    exposure = new Dictionary<string, string> { ["LoanAccountNumber"] = "0", ["AmountOutstanding"] = "2" }
                }
            }
        };

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/discover/mapping", mappingBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var recorded = _factory.MappingStore.RunMappings.Skip(before)
            .Where(m => m.RunId == Guid.Parse(runId)).ToList();

        // no empty write-off row is written for a file that does not exist
        Assert.All(recorded, m => Assert.Equal("exposure", m.FileKind));
        Assert.Contains(recorded, m => m.Mapping["LoanAccountNumber"] == "0");
    }

    [Fact]
    public async Task TestConfirmingAMappingForAnUnknownRunIs404()
    {
        HttpClient client = _factory.CreateClient();

        var mappingBody = new { run_id = Guid.NewGuid().ToString(), sets = Array.Empty<object>() };

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/discover/mapping", mappingBody);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TestRunningWithAConfirmedMappingWiresColumnMapsWithoutThrowing()
    {
        HttpClient client = _factory.CreateClient();

        // AddFullSet's debug.zip is deliberately not a real zip (see its comment),
        // so this set never actually discovers - job.MappableFiles/ColumnMaps
        // stay empty for it, same as a run where no mapping was ever confirmed.
        // That is fine here: the point of this test is only that /api/run still
        // wires an (empty) columnMaps dictionary into engine.Run without
        // throwing, not that a mapping was actually applied - see
        // ReconciliationEngineMappingTests for that, against real engine input.
        using MultipartFormDataContent discoverForm = new();
        AddFullSet(discoverForm, 0, "JUL2026.csv");
        HttpResponseMessage discoverResponse = await client.PostAsync("/api/discover", discoverForm);
        using JsonDocument discoverDoc = JsonDocument.Parse(await discoverResponse.Content.ReadAsStringAsync());
        string runId = discoverDoc.RootElement.GetProperty("run_id").GetString()!;
        string setKey = HazardRecon.Core.Services.InputDiscoverer.SetKeyFromFolder("JUL2026");

        var mappingBody = new
        {
            run_id = runId,
            sets = new[]
            {
                new
                {
                    key = setKey,
                    writeoff = new Dictionary<string, string>
                    {
                        ["LoanAccountNumber"] = "LoanAccountNumber", ["CustomerId"] = "CustomerId",
                        ["Amount"] = "Amount", ["ReportDate"] = "ReportDate"
                    },
                    exposure = new Dictionary<string, string> { ["LoanAccountNumber"] = "0", ["AmountOutstanding"] = "2" }
                }
            }
        };
        HttpResponseMessage mapResponse = await client.PostAsJsonAsync("/api/discover/mapping", mappingBody);
        Assert.Equal(HttpStatusCode.OK, mapResponse.StatusCode);

        HttpResponseMessage runResponse = await client.PostAsJsonAsync("/api/run", new { run_id = runId });
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);

        // poll briefly - the engine runs on a background Task.Run
        JsonElement job = default;
        for (int i = 0; i < 50; i++)
        {
            HttpResponseMessage jobResponse = await client.GetAsync($"/api/job/{runId}");
            Assert.Equal(HttpStatusCode.OK, jobResponse.StatusCode);
            using JsonDocument jobDoc = JsonDocument.Parse(await jobResponse.Content.ReadAsStringAsync());
            job = jobDoc.RootElement.Clone();
            if (job.GetProperty("status").GetString() != "running") break;
            await Task.Delay(50);
        }

        // deterministic given the placeholder "zipbytes" debug file: discovery
        // finds zero valid sets, so the engine throws before ever reaching
        // DataLoaders - reaching this exact status, rather than a 500, proves
        // engine.Run(..., columnMaps: capturedJob.ColumnMaps) is wired correctly
        Assert.Equal("error", job.GetProperty("status").GetString());
        Assert.Contains("No analysis sets found", job.GetProperty("error").GetString());
    }
}
