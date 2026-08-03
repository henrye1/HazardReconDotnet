using System.Diagnostics;
using HazardRecon.Web;
using Xunit;

namespace HazardRecon.Tests.Web;

public class JobStateLogConcurrencyTests
{
    [Fact]
    public void TestTheLogSurvivesBeingReadWhileAWriterIsStillAppending()
    {
        // GET /api/job/{rid} enumerates job.Log (via a LINQ Select, materialized
        // during JSON serialization) while the background run's Logger() is
        // still appending to the same instance - a plain List<T> throws
        // "Collection was modified" under this exact interleaving.
        JobState job = new();

        using CancellationTokenSource cts = new();
        // a dedicated thread, not a ThreadPool worker - a busy-looping pool
        // thread can starve other tests' own Task.Run-based background work
        // when the suite runs with test-collection parallelism. Capped at a
        // realistic log volume: an unbounded writer would make every
        // .ToList() read below cost O(queue length), a runaway quadratic
        // blowup that looks like a hang but is really just unbounded work.
        Thread writer = new(() =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested && i < 5_000)
            {
                job.Log.Enqueue(new JobLogEntry(DateTimeOffset.UtcNow, $"line {i++}", "info"));
            }
        });
        writer.Start();

        Stopwatch sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 500)
        {
            List<string> snapshot = job.Log.Select(l => l.Message).ToList();
            Assert.NotNull(snapshot);
        }

        cts.Cancel();
        writer.Join();
    }
}
