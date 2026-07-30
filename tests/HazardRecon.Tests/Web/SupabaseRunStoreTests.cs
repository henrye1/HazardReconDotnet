using System.Net;
using HazardRecon.Tests.Llm;
using HazardRecon.Web.Runs;
using HazardRecon.Web.Supabase;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseRunStoreTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string OneRunJson = """
    [
      {
        "id": "22222222-2222-2222-2222-222222222222",
        "user_id": "11111111-1111-1111-1111-111111111111",
        "status": "ready",
        "model_id": null,
        "set_labels": ["JUN2026 0.5PCT"],
        "error": null,
        "created_at": "2026-07-30T09:00:00+00:00",
        "started_at": null,
        "finished_at": null
      }
    ]
    """;

    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co",
        AnonKey = "anon-key",
        ServiceRoleKey = "service-key"
    };

    private static SupabaseRunStore Store(FakeHttpMessageHandler handler) =>
        new(new SupabaseRestClient(Options(), handler));

    [Fact]
    public async Task TestCreateInsertsAndReturnsTheRow()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.Created, OneRunJson));

        RunRecord run = await Store(handler).CreateAsync(UserId, new[] { "JUN2026 0.5PCT" });

        Assert.Equal(RunId, run.Id);
        Assert.Equal(UserId, run.UserId);
        Assert.Equal("ready", run.Status);
        Assert.Equal(new[] { "JUN2026 0.5PCT" }, run.SetLabels);

        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("https://ref.supabase.co/rest/v1/runs", handler.Requests[0].Url);
        Assert.Contains("JUN2026 0.5PCT", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TestGetFiltersByBothRunAndUser()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, OneRunJson));

        await Store(handler).GetAsync(RunId, UserId);

        string url = handler.Requests[0].Url;
        Assert.Contains($"id=eq.{RunId}", url);
        Assert.Contains($"user_id=eq.{UserId}", url);
    }

    [Fact]
    public async Task TestGetReturnsNullWhenNoRowMatches()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        Assert.Null(await Store(handler).GetAsync(RunId, UserId));
    }

    [Fact]
    public async Task TestListIsScopedToTheUserAndNewestFirst()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, OneRunJson));

        IReadOnlyList<RunRecord> runs = await Store(handler).ListAsync(UserId);

        Assert.Single(runs);
        string url = handler.Requests[0].Url;
        Assert.Contains($"user_id=eq.{UserId}", url);
        Assert.Contains("order=created_at.desc", url);
        Assert.Contains("limit=50", url);
    }

    [Fact]
    public async Task TestUpdateStatusPatchesTheRun()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, OneRunJson));

        await Store(handler).UpdateStatusAsync(RunId, "error", "Boom: it broke");

        Assert.Equal("PATCH", handler.Requests[0].Method);
        Assert.Contains($"id=eq.{RunId}", handler.Requests[0].Url);
        Assert.Contains("\"status\":\"error\"", handler.Requests[0].Body);
        Assert.Contains("Boom: it broke", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TestCountSinceCountsReturnedRows()
    {
        FakeHttpMessageHandler handler = new((_, _) =>
            (HttpStatusCode.OK, """[{"id":"a"},{"id":"b"},{"id":"c"}]"""));

        int count = await Store(handler).CountSinceAsync(
            UserId, new DateTimeOffset(2026, 7, 29, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal(3, count);
        Assert.Contains("created_at=gte.", handler.Requests[0].Url);
        // the timestamp must be percent-encoded: a bare '+' would decode as a space
        Assert.DoesNotContain("+00:00", handler.Requests[0].Url);
    }

    [Fact]
    public async Task TestMarkRunningAsInterruptedPatchesOnlyRunningRows()
    {
        FakeHttpMessageHandler handler = new((_, _) =>
            (HttpStatusCode.OK, """[{"id":"a"},{"id":"b"}]"""));

        int changed = await Store(handler).MarkRunningAsInterruptedAsync();

        Assert.Equal(2, changed);
        Assert.Equal("PATCH", handler.Requests[0].Method);
        Assert.Contains("status=eq.running", handler.Requests[0].Url);
        Assert.Contains("\"status\":\"interrupted\"", handler.Requests[0].Body);
    }
}
