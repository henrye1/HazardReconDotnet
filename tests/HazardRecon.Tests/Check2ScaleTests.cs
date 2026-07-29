using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

/// <summary>
/// Check 2 runs against the full scored population (hundreds of thousands of
/// accounts) crossed with the whole write-off file. Any per-account scan of the
/// write-off list makes a real run effectively never finish, which shows up in
/// the web UI as a job stuck on "running" straight after the migrations line.
/// </summary>
public class Check2ScaleTests
{
    private const int ScoredCount = 200_000;
    private const int WriteOffCount = 100_000;

    [Fact]
    public async Task TestCheck2CompletesOnProductionSizedPopulations()
    {
        HashSet<string> scored = new();
        for (int i = 0; i < ScoredCount; i++) scored.Add($"ACC{i}");

        List<WriteOffAggRecord> woAgg = new(WriteOffCount);
        for (int i = 0; i < WriteOffCount; i++)
        {
            woAgg.Add(new WriteOffAggRecord
            {
                AccountNormalized = $"ACC{i}",
                CustomerId = $"C{i}",
                WriteOffAmount = 10.0,
                FirstWriteOffDate = new DateTime(2026, 3, 31),
                LastWriteOffDate = new DateTime(2026, 3, 31),
                WriteOffRows = 1
            });
        }

        // Nothing defaulted, so every written-off account is a check 2 candidate.
        HashSet<string> defaultAccts = new();
        (DateTime? Lo, DateTime? Hi) window = (new DateTime(2025, 12, 1), new DateTime(2026, 6, 30));

        ReconciliationEngine engine = new();
        ReconciliationSummary? summary = null;

        Task work = Task.Run(() =>
        {
            var (_, s) = engine.ReconcileWriteoffNotDefault(scored, woAgg, defaultAccts, window);
            summary = s;
        });

        Task finishedTask = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(30)));
        bool finished = ReferenceEquals(finishedTask, work);

        Assert.True(finished,
            $"Check 2 did not finish within 30s for {ScoredCount:N0} scored x {WriteOffCount:N0} " +
            "write-off accounts - ScoredInWriteOff is scanning the write-off list once per scored account.");

        Assert.Equal(WriteOffCount, summary!.ScoredInWriteOff);
        Assert.Equal(WriteOffCount, summary.WoNotDefaultTotal);
        Assert.Equal(WriteOffCount, summary.WoInWindow);
    }
}
