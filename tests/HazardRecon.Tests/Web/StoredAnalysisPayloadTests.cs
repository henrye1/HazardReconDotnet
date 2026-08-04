using System.Text.Json;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>
/// Pins the payload rebuilt from the stored tables against the one a live run
/// sends, through a real engine run and the real mapper. A question asked about a
/// run must be answered from the same figures whether or not the server has
/// restarted since, and this is what would catch either side drifting.
/// </summary>
public class StoredAnalysisPayloadTests : IClassFixture<SyntheticDataFixture>
{
    private static readonly Guid RunId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly SyntheticDataFixture _fixture;

    public StoredAnalysisPayloadTests(SyntheticDataFixture fixture) => _fixture = fixture;

    private Dictionary<string, SingleSetResult> RunEngine(string outDir)
    {
        ReconciliationEngine engine = new();
        ReconciliationRunResult result = engine.Run(
            _fixture.RootDir, Path.Combine(_fixture.OutDir, outDir),
            logger: (_, _) => { }, analyze: false, analyst: null);

        return result.Results;
    }

    /// <summary>The run as it comes back from the database, via the mapper that wrote it.</summary>
    private static RunRecord StoredForm(Dictionary<string, SingleSetResult> results) => new()
    {
        Id = RunId,
        UserId = UserId,
        RunSetResults = results
            .Select(kv => RunSetResultMapper.Build(RunId, UserId, kv.Key, kv.Value))
            .ToList()
    };

    private static string Json(object value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

    [Fact]
    public void TestTheRebuiltPayloadMatchesWhatALiveRunSends()
    {
        Dictionary<string, SingleSetResult> results = RunEngine("stored-payload-equal");

        Dictionary<string, object> live = AiAnalysisService.BuildAnalysisPayload(results);
        Dictionary<string, object> rebuilt = StoredAnalysisPayload.Build(StoredForm(results));

        Assert.Equal(Json(live), Json(rebuilt));
    }

    [Fact]
    public void TestEverySetSurvivesTheRoundTrip()
    {
        Dictionary<string, SingleSetResult> results = RunEngine("stored-payload-sets");

        Dictionary<string, object> rebuilt = StoredAnalysisPayload.Build(StoredForm(results));

        var sets = Assert.IsType<List<Dictionary<string, object?>>>(rebuilt["sets"]);
        Assert.Equal(results.Count, sets.Count);
        Assert.Equal(results.Keys.ToList(), sets.Select(s => (string)s["key"]!).ToList());
    }

    [Fact]
    public void TestARunWithNoStoredSetsRebuildsToAnEmptyPayload()
    {
        // an errored or interrupted run has no set results; chat should get an
        // empty shape rather than throw
        Dictionary<string, object> rebuilt = StoredAnalysisPayload.Build(new RunRecord { Id = RunId, UserId = UserId });

        var sets = Assert.IsType<List<Dictionary<string, object?>>>(rebuilt["sets"]);
        Assert.Empty(sets);
    }

    [Fact]
    public void TestTheKeysAreExactlyTheOnesTheChatPromptNames()
    {
        // ChatService's system prompt tells the model to read untraced, trace_rate
        // and the check2_ figures by name; losing one silently would make it answer
        // "the figures do not contain that"
        Dictionary<string, SingleSetResult> results = RunEngine("stored-payload-keys");
        var sets = (List<Dictionary<string, object?>>)StoredAnalysisPayload.Build(StoredForm(results))["sets"];

        foreach (string key in new[]
        {
            "key", "label", "window", "defaults", "default_exposure", "traced_writeoff", "traced_ifrs9",
            "untraced", "untraced_exposure", "untraced_fully_recovered", "untraced_fully_recovered_amount",
            "trace_rate", "check2_total", "check2_in_window", "check2_in_window_amount", "check2_post_window",
            "check2_pre_window", "in_window_last_bucket_hist", "scored_distinct", "writeoff_distinct",
            "ifrs9_distinct", "ifrs9_key_overlap", "migration_matrix", "migration_validation", "engine_params",
        })
        {
            Assert.True(sets[0].ContainsKey(key), $"the rebuilt payload lost '{key}'");
        }
    }

    [Fact]
    public void TestAnUnknownLastBucketIsTheOneFigureThatCannotComeBack()
    {
        // Pinned because it is the single known divergence: the live histogram files
        // an in-window write-off with no last bucket under "unknown", while
        // run_set_last_bucket_rows never stores those rows, so a rebuild cannot
        // reproduce the entry. Everything else round-trips exactly.
        Dictionary<string, SingleSetResult> results = new()
        {
            ["JUN2026"] = new SingleSetResult
            {
                Summary = new ReconciliationSummary { Label = "JUN2026" },
                WoNd = new List<WriteOffNotDefaultRecord>
                {
                    new() { AccountNumber = "A1", WriteOffVsScoringWindow = "IN WINDOW", LastBucketRating = "5", WriteOffAmount = 10 },
                    new() { AccountNumber = "A2", WriteOffVsScoringWindow = "IN WINDOW", LastBucketRating = null, WriteOffAmount = 20 },
                }
            }
        };

        var liveSets = (List<Dictionary<string, object?>>)AiAnalysisService.BuildAnalysisPayload(results)["sets"];
        var liveHist = (Dictionary<string, int>)liveSets[0]["in_window_last_bucket_hist"]!;

        var rebuiltSets = (List<Dictionary<string, object?>>)StoredAnalysisPayload.Build(StoredForm(results))["sets"];
        var rebuiltHist = (Dictionary<string, int>)rebuiltSets[0]["in_window_last_bucket_hist"]!;

        // the rated account survives on both sides
        Assert.Equal(1, liveHist["5"]);
        Assert.Equal(1, rebuiltHist["5"]);

        // the unrated one is "unknown" live, and simply absent once stored
        Assert.Equal(1, liveHist["unknown"]);
        Assert.DoesNotContain("unknown", rebuiltHist.Keys);
    }

    [Fact]
    public void TestTheAllMonthsMatrixIsTheOneCarriedOver()
    {
        // stored per month; only the all-months matrix belongs in this payload, and
        // it is the one that reconciles to the engine's own accumulated arrays
        Dictionary<string, SingleSetResult> results = RunEngine("stored-payload-matrix");
        RunSetResultRecord rec = StoredForm(results).RunSetResults![0];

        Assert.Contains(rec.MigrationCells!, c => c.MonthLabel == "All months");
        Assert.Contains(rec.MigrationCells!, c => c.MonthLabel != "All months");

        var sets = (List<Dictionary<string, object?>>)StoredAnalysisPayload.Build(StoredForm(results))["sets"];
        var matrix = sets[0]["migration_matrix"] as Dictionary<string, object>;

        Assert.NotNull(matrix);
        var counts = Assert.IsType<List<List<int>>>(matrix!["from_to_counts"]);
        Assert.Equal(6, counts.Count);
        Assert.All(counts, row => Assert.Equal(6, row.Count));

        // and it equals what the live payload carries for the same run
        var liveSets = (List<Dictionary<string, object?>>)AiAnalysisService.BuildAnalysisPayload(results)["sets"];
        Assert.Equal(Json(((Dictionary<string, object>)liveSets[0]["migration_matrix"]!)["from_to_counts"]),
                     Json(counts));
    }
}
