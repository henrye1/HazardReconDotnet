using System.Net;
using HazardRecon.Tests.Llm;
using HazardRecon.Web.Runs;
using HazardRecon.Web.Supabase;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseColumnMappingStoreTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co", AnonKey = "anon-key", ServiceRoleKey = "service-key"
    };

    private static SupabaseColumnMappingStore Store(FakeHttpMessageHandler handler) =>
        new(new SupabaseRestClient(Options(), handler));

    [Fact]
    public async Task TestGetSavedMappingReturnsFieldToColumnsDictionary()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK,
            """[{"field_name":"LoanAccountNumber","source_column":"Column 1","ordinal":0},{"field_name":"AmountOutstanding","source_column":"Column 3","ordinal":0}]"""));

        IReadOnlyDictionary<string, IReadOnlyList<string>> mapping =
            await Store(handler).GetSavedMappingAsync(UserId, "exposure", "abc123");

        Assert.Equal(new[] { "Column 1" }, mapping["LoanAccountNumber"]);
        Assert.Equal(new[] { "Column 3" }, mapping["AmountOutstanding"]);
        Assert.Contains($"user_id=eq.{UserId}", handler.Requests[0].Url);
        Assert.Contains("file_kind=eq.exposure", handler.Requests[0].Url);
        Assert.Contains("column_signature=eq.abc123", handler.Requests[0].Url);
    }

    /// <summary>
    /// Several rows for one field are its columns in ordinal order - and the order
    /// is restored here rather than trusted from the response, since it is what the
    /// user picked and what the mapper card shows back.
    /// </summary>
    [Fact]
    public async Task TestGetSavedMappingGroupsAMultiColumnFieldInOrdinalOrder()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK,
            """[{"field_name":"AgingBuckets","source_column":"90 Days","ordinal":1},{"field_name":"AgingBuckets","source_column":"60 Days","ordinal":0}]"""));

        IReadOnlyDictionary<string, IReadOnlyList<string>> mapping =
            await Store(handler).GetSavedMappingAsync(UserId, "age_analysis", "abc123");

        Assert.Equal(new[] { "60 Days", "90 Days" }, mapping["AgingBuckets"]);
    }

    [Fact]
    public async Task TestGetSavedMappingReturnsEmptyWhenNoneSaved()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        IReadOnlyDictionary<string, IReadOnlyList<string>> mapping =
            await Store(handler).GetSavedMappingAsync(UserId, "writeoff", "xyz");

        Assert.Empty(mapping);
    }

    /// <summary>
    /// Delete-then-insert per field, not an upsert. An upsert would leave a row
    /// behind for every column the user has deselected since last time, so a bucket
    /// they removed would silently come back on the next upload.
    /// </summary>
    [Fact]
    public async Task TestSaveMappingReplacesTheFieldRatherThanUpserting()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        await Store(handler).SaveMappingAsync(UserId, "writeoff", "sig1",
            new Dictionary<string, IReadOnlyList<string>> { ["Amount"] = new[] { "Amount" } });

        Assert.Equal("DELETE", handler.Requests[0].Method);
        Assert.Contains("field_name=eq.Amount", handler.Requests[0].Url);
        Assert.Contains("column_signature=eq.sig1", handler.Requests[0].Url);

        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.Contains("\"source_column\":\"Amount\"", handler.Requests[1].Body);
    }

    [Fact]
    public async Task TestSaveMappingSendsAnOrdinalPerColumn()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        await Store(handler).SaveMappingAsync(UserId, "age_analysis", "sig1",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["AgingBuckets"] = new[] { "60 Days", "90 Days" }
            });

        string body = handler.Requests.Last().Body ?? "";
        Assert.Contains("\"source_column\":\"60 Days\",\"ordinal\":0", body);
        Assert.Contains("\"source_column\":\"90 Days\",\"ordinal\":1", body);
    }

    [Fact]
    public async Task TestSaveMappingWithNoEntriesSendsNothing()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        await Store(handler).SaveMappingAsync(UserId, "writeoff", "sig1",
            new Dictionary<string, IReadOnlyList<string>>());

        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// An empty selection still clears what was saved: the user deselecting every
    /// bucket has to be recordable, or the old choice comes back next time.
    /// </summary>
    [Fact]
    public async Task TestSavingAnEmptySelectionStillClearsTheField()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        await Store(handler).SaveMappingAsync(UserId, "age_analysis", "sig1",
            new Dictionary<string, IReadOnlyList<string>> { ["AgingBuckets"] = Array.Empty<string>() });

        Assert.Equal("DELETE", handler.Requests[0].Method);
        Assert.Contains("field_name=eq.AgingBuckets", handler.Requests[0].Url);
        // nothing to insert afterwards
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TestRecordRunMappingDeletesThenInsertsForTheSetAndFileKind()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));
        Guid runId = Guid.NewGuid();

        await Store(handler).RecordRunMappingAsync(runId, "JUN2026", "exposure",
            new Dictionary<string, IReadOnlyList<string>> { ["LoanAccountNumber"] = new[] { "Column 1" } });

        Assert.Equal("DELETE", handler.Requests[0].Method);
        Assert.Contains($"run_id=eq.{runId}", handler.Requests[0].Url);
        Assert.Contains("set_key=eq.JUN2026", handler.Requests[0].Url);
        Assert.Contains("file_kind=eq.exposure", handler.Requests[0].Url);
        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.Contains("\"source_column\":\"Column 1\"", handler.Requests[1].Body);
    }
}
