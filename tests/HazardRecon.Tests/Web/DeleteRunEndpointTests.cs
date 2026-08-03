using System.Net;
using System.Security.Claims;
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

/// <summary>Drives DELETE /api/runs/{rid}.</summary>
public class DeleteRunEndpointTests : IClassFixture<DeleteRunEndpointTests.AuthedFactory>
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");

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
        public FakeRunFileStore RunFileStore { get; } = new();
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
                services.AddSingleton<IRunStore>(RunStore);
                services.AddSingleton<IRunFileStore>(RunFileStore);
                services.AddSingleton<IFileStore>(FileStore);
            });

            return base.CreateHost(builder);
        }
    }

    private readonly AuthedFactory _factory;

    public DeleteRunEndpointTests(AuthedFactory factory) => _factory = factory;

    private async Task<RunRecord> SeedAsync(Guid? owner = null, string status = RunStatus.Done)
    {
        RunRecord run = await _factory.RunStore.CreateAsync(owner ?? User, new[] { "JUN2026" });
        run.StatusId = RunStatus.IdOf(status);
        return run;
    }

    [Fact]
    public async Task Deleting_a_run_reports_ok_and_the_run_is_gone()
    {
        HttpClient client = _factory.CreateClient();
        RunRecord run = await SeedAsync();

        HttpResponseMessage response = await client.DeleteAsync($"/api/runs/{run.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await _factory.RunStore.GetAsync(run.Id, User));

        // and it can no longer be reopened
        HttpResponseMessage reopened = await client.GetAsync($"/api/runs/{run.Id}");
        Assert.Equal(HttpStatusCode.NotFound, reopened.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_run_removes_its_stored_objects()
    {
        HttpClient client = _factory.CreateClient();
        RunRecord run = await SeedAsync();

        string path = $"{User}/{run.Id}/output/hazard_rate_reconciliation.xlsx";
        using (MemoryStream content = new(new byte[] { 1 }))
        {
            await _factory.FileStore.UploadAsync(path, content, "application/octet-stream");
        }
        await _factory.RunFileStore.AddAsync(new[]
        {
            new RunFileRecord
            {
                RunId = run.Id, UserId = User, Kind = "output",
                RelativePath = "hazard_rate_reconciliation.xlsx", StoragePath = path
            }
        });

        await client.DeleteAsync($"/api/runs/{run.Id}");

        Assert.DoesNotContain(path, _factory.FileStore.Objects.Keys);
    }

    [Fact]
    public async Task A_run_that_is_still_going_is_refused_rather_than_raced()
    {
        HttpClient client = _factory.CreateClient();
        RunRecord run = await SeedAsync(status: RunStatus.Running);

        HttpResponseMessage response = await client.DeleteAsync($"/api/runs/{run.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("still going", await response.Content.ReadAsStringAsync());
        Assert.NotNull(await _factory.RunStore.GetAsync(run.Id, User));
    }

    [Fact]
    public async Task Another_users_run_is_a_404_and_survives()
    {
        HttpClient client = _factory.CreateClient();
        Guid someoneElse = Guid.Parse("33333333-3333-3333-3333-333333333333");
        RunRecord run = await SeedAsync(owner: someoneElse);

        HttpResponseMessage response = await client.DeleteAsync($"/api/runs/{run.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(await _factory.RunStore.GetAsync(run.Id, someoneElse));
    }

    [Fact]
    public async Task An_unknown_run_is_a_404()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.DeleteAsync($"/api/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_id_that_is_not_a_guid_is_a_404_rather_than_a_500()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.DeleteAsync("/api/runs/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
