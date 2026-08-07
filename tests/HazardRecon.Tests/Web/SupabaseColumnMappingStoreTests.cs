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
    public async Task TestGetSavedMappingReturnsFieldToColumnDictionary()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK,
            """[{"field_name":"LoanAccountNumber","source_column":"Column 1"},{"field_name":"AmountOutstanding","source_column":"Column 3"}]"""));

        IReadOnlyDictionary<string, string> mapping =
            await Store(handler).GetSavedMappingAsync(UserId, "exposure", "abc123");

        Assert.Equal("Column 1", mapping["LoanAccountNumber"]);
        Assert.Equal("Column 3", mapping["AmountOutstanding"]);
        Assert.Contains($"user_id=eq.{UserId}", handler.Requests[0].Url);
        Assert.Contains("file_kind=eq.exposure", handler.Requests[0].Url);
        Assert.Contains("column_signature=eq.abc123", handler.Requests[0].Url);
    }

    [Fact]
    public async Task TestGetSavedMappingReturnsEmptyWhenNoneSaved()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        IReadOnlyDictionary<string, string> mapping =
            await Store(handler).GetSavedMappingAsync(UserId, "writeoff", "xyz");

        Assert.Empty(mapping);
    }

    [Fact]
    public async Task TestSaveMappingUpsertsWithMergeDuplicates()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        await Store(handler).SaveMappingAsync(UserId, "writeoff", "sig1",
            new Dictionary<string, string> { ["Amount"] = "Amount" });

        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Contains("on_conflict=user_id,file_kind,column_signature,field_name", handler.Requests[0].Url);
        Assert.Contains("\"source_column\":\"Amount\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TestSaveMappingWithNoEntriesSendsNothing()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        await Store(handler).SaveMappingAsync(UserId, "writeoff", "sig1", new Dictionary<string, string>());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TestRecordRunMappingDeletesThenInsertsForTheSetAndFileKind()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));
        Guid runId = Guid.NewGuid();

        await Store(handler).RecordRunMappingAsync(runId, "JUN2026", "exposure",
            new Dictionary<string, string> { ["LoanAccountNumber"] = "Column 1" });

        Assert.Equal("DELETE", handler.Requests[0].Method);
        Assert.Contains($"run_id=eq.{runId}", handler.Requests[0].Url);
        Assert.Contains("set_key=eq.JUN2026", handler.Requests[0].Url);
        Assert.Contains("file_kind=eq.exposure", handler.Requests[0].Url);
        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.Contains("\"source_column\":\"Column 1\"", handler.Requests[1].Body);
    }

    [Fact]
    public async Task TestRecordRunMappingCarriesTheConfirmedHeaderReading()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));
        Guid runId = Guid.NewGuid();

        await Store(handler).RecordRunMappingAsync(runId, "JUN2026", "exposure",
            new Dictionary<string, string> { ["LoanAccountNumber"] = "Column 1" }, hasHeaders: false);

        Assert.Contains("\"has_headers\":false", handler.Requests[1].Body);
    }

    [Fact]
    public async Task TestGetRunHasHeadersReadsBackWhatWasRecorded()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, """[{"has_headers":false}]"""));

        bool? hasHeaders = await Store(handler).GetRunHasHeadersAsync(Guid.NewGuid(), "JUN2026", "exposure");

        Assert.False(hasHeaders);
        Assert.Contains("set_key=eq.JUN2026", handler.Requests[0].Url);
        Assert.Contains("file_kind=eq.exposure", handler.Requests[0].Url);
    }

    [Fact]
    public async Task TestGetRunHasHeadersIsNullWhenNothingWasRecorded()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        bool? hasHeaders = await Store(handler).GetRunHasHeadersAsync(Guid.NewGuid(), "JUN2026", "exposure");

        Assert.Null(hasHeaders);
    }
}
