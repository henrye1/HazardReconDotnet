using System.Net;
using System.Security.Claims;
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

/// <summary>Drives GET /api/runs/{rid}/inputs.</summary>
public class RunInputsEndpointTests : IClassFixture<RunInputsEndpointTests.AuthedFactory>
{
    private static readonly Guid TestUser = Guid.Parse("11111111-1111-1111-1111-111111111111");

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
        public FakeRunStore Runs { get; } = new();
        public FakeRunFileStore RunFiles { get; } = new();
        public FakeFileStore FileStore { get; } = new();

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
                services.AddSingleton<IRunStore>(Runs);
                services.AddSingleton<IRunFileStore>(RunFiles);
                services.AddSingleton<IFileStore>(FileStore);
            });

            return base.CreateHost(builder);
        }
    }

    private readonly AuthedFactory _factory;
    private HttpClient Client => _factory.CreateClient();

    public RunInputsEndpointTests(AuthedFactory factory) => _factory = factory;

    private Guid SeedRun(IReadOnlyList<string> setLabels, Guid? userId = null,
        DateTimeOffset? inputsPurgedAt = null, string status = RunStatus.Ready)
    {
        Guid id = Guid.NewGuid();
        _factory.Runs.Runs.Add(new RunRecord
        {
            Id = id,
            UserId = userId ?? TestUser,
            StatusId = RunStatus.IdOf(status),
            SetLabels = setLabels.ToList(),
            InputsPurgedAt = inputsPurgedAt
        });
        return id;
    }

    private void SeedInput(Guid runId, string relativePath, string role, string originalName, long size)
    {
        _factory.RunFiles.Files.Add(new RunFileRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            UserId = TestUser,
            Kind = "input",
            RelativePath = relativePath,
            StoragePath = $"{TestUser}/{runId}/input/{relativePath}",
            SizeBytes = size,
            Role = role,
            OriginalName = originalName
        });
    }

    [Fact]
    public async Task TestListsEachSetsStoredInputs()
    {
        Guid run = SeedRun(new[] { "JUNE 2026" });
        SeedInput(run, "0/IFRS9.csv", "exposure", "IFRS9 FILE JUNE 2025.csv", 12_800_000);
        SeedInput(run, "0/debug.zip", "debug", "debug.zip", 13_800_000);

        HttpResponseMessage res = await Client.GetAsync($"/api/runs/{run}/inputs");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());

        Assert.False(doc.RootElement.GetProperty("inputs_purged").GetBoolean());
        JsonElement set = doc.RootElement.GetProperty("sets")[0];
        Assert.Equal(0, set.GetProperty("index").GetInt32());
        Assert.Equal("JUNE 2026", set.GetProperty("label").GetString());

        JsonElement exposure = set.GetProperty("files").EnumerateArray()
            .Single(f => f.GetProperty("role").GetString() == "exposure");
        Assert.Equal("IFRS9 FILE JUNE 2025.csv", exposure.GetProperty("name").GetString());
        Assert.Equal(12_800_000, exposure.GetProperty("size_bytes").GetInt64());
    }

    [Fact]
    public async Task TestAPurgedRunSaysSoAndListsNoFiles()
    {
        Guid run = SeedRun(new[] { "MAY 2026" }, inputsPurgedAt: DateTimeOffset.UtcNow);

        HttpResponseMessage res = await Client.GetAsync($"/api/runs/{run}/inputs");

        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("inputs_purged").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("sets").EnumerateArray());
    }

    [Fact]
    public async Task TestAnotherUsersRunIsNotFound()
    {
        Guid run = SeedRun(new[] { "X" }, userId: Guid.NewGuid());

        HttpResponseMessage res = await Client.GetAsync($"/api/runs/{run}/inputs");

        // 404 not 403: a 403 would confirm the run exists
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task TestAnUnknownRunIsNotFound()
    {
        HttpResponseMessage res = await Client.GetAsync($"/api/runs/{Guid.NewGuid()}/inputs");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Theory]
    [InlineData(RunStatus.Ready)]
    [InlineData(RunStatus.Error)]
    [InlineData(RunStatus.Interrupted)]
    [InlineData(RunStatus.Done)]
    public async Task TestWorksTheSameRegardlessOfStatus(string status)
    {
        Guid run = SeedRun(new[] { "JUNE 2026" }, status: status);
        SeedInput(run, "0/IFRS9.csv", "exposure", "IFRS9.csv", 10);

        HttpResponseMessage res = await Client.GetAsync($"/api/runs/{run}/inputs");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("inputs_purged").GetBoolean());
        Assert.Single(doc.RootElement.GetProperty("sets").EnumerateArray());
    }
}
