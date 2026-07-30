using System.Text;
using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

public class InputPurgerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static RunRecord Aged(int daysOld) => new()
    {
        Id = Guid.NewGuid(),
        UserId = UserId,
        Status = "done",
        CreatedAt = Now.AddDays(-daysOld)
    };

    private static void Seed(FakeFileStore storage, FakeRunFileStore index, RunRecord run)
    {
        foreach (string kind in new[] { "input", "output" })
        {
            string path = $"{run.UserId}/{run.Id}/{kind}/file.csv";
            storage.UploadAsync(path, new MemoryStream(Encoding.UTF8.GetBytes("x")), "text/csv").Wait();
            index.Files.Add(new RunFileRecord
            {
                RunId = run.Id, UserId = run.UserId, Kind = kind,
                RelativePath = "file.csv", StoragePath = path, SizeBytes = 1
            });
        }
    }

    [Fact]
    public async Task TestInputsOlderThanThirtyDaysArePurged()
    {
        FakeRunStore runs = new();
        FakeFileStore storage = new();
        FakeRunFileStore index = new();

        RunRecord old = Aged(31);
        runs.Runs.Add(old);
        Seed(storage, index, old);

        InputPurger.PurgeOutcome outcome = await new InputPurger(runs, index, storage).PurgeAsync(Now);

        Assert.Equal(1, outcome.Purged);
        Assert.Empty(outcome.Failed);
        Assert.Contains(old.Id, runs.Stamped);
        Assert.DoesNotContain(storage.Objects.Keys, k => k.Contains("/input/"));
        Assert.DoesNotContain(index.Files, f => f.Kind == "input");
    }

    [Fact]
    public async Task TestOutputsSurviveThePurge()
    {
        // outputs and metadata are small and kept forever - only inputs go
        FakeRunStore runs = new();
        FakeFileStore storage = new();
        FakeRunFileStore index = new();

        RunRecord old = Aged(45);
        runs.Runs.Add(old);
        Seed(storage, index, old);

        await new InputPurger(runs, index, storage).PurgeAsync(Now);

        Assert.Contains(storage.Objects.Keys, k => k.Contains("/output/"));
        Assert.Contains(index.Files, f => f.Kind == "output");
    }

    [Fact]
    public async Task TestRecentRunsAreLeftAlone()
    {
        FakeRunStore runs = new();
        FakeFileStore storage = new();
        FakeRunFileStore index = new();

        RunRecord recent = Aged(29);
        runs.Runs.Add(recent);
        Seed(storage, index, recent);

        InputPurger.PurgeOutcome outcome = await new InputPurger(runs, index, storage).PurgeAsync(Now);

        Assert.Equal(0, outcome.Purged);
        Assert.Contains(storage.Objects.Keys, k => k.Contains("/input/"));
    }

    [Fact]
    public async Task TestOnlyTheRunsOwnInputsAreDeleted()
    {
        FakeRunStore runs = new();
        FakeFileStore storage = new();
        FakeRunFileStore index = new();

        RunRecord old = Aged(40);
        RunRecord recent = Aged(1);
        runs.Runs.Add(old);
        runs.Runs.Add(recent);
        Seed(storage, index, old);
        Seed(storage, index, recent);

        await new InputPurger(runs, index, storage).PurgeAsync(Now);

        // a prefix delete is easy to get wrong by one path segment
        Assert.DoesNotContain(storage.Objects.Keys, k => k.Contains($"{old.Id}/input/"));
        Assert.Contains(storage.Objects.Keys, k => k.Contains($"{recent.Id}/input/"));
    }

    [Fact]
    public async Task TestAFailedRunIsRetriedRatherThanMarkedDone()
    {
        // the stamp goes last on purpose: marking a run purged when the delete
        // failed would strand its inputs in storage forever
        FakeRunStore runs = new();
        FakeFileStore storage = new();
        FakeRunFileStore index = new();

        RunRecord old = Aged(60);
        runs.Runs.Add(old);
        Seed(storage, index, old);
        runs.FailStampFor = old.Id;

        InputPurger.PurgeOutcome outcome = await new InputPurger(runs, index, storage).PurgeAsync(Now);

        Assert.Equal(0, outcome.Purged);
        Assert.Single(outcome.Failed);
        Assert.Null(old.InputsPurgedAt);
    }

    [Fact]
    public async Task TestOneFailureDoesNotStopTheSweep()
    {
        FakeRunStore runs = new();
        FakeFileStore storage = new();
        FakeRunFileStore index = new();

        RunRecord bad = Aged(50);
        RunRecord good = Aged(50);
        runs.Runs.Add(bad);
        runs.Runs.Add(good);
        Seed(storage, index, bad);
        Seed(storage, index, good);
        runs.FailStampFor = bad.Id;

        InputPurger.PurgeOutcome outcome = await new InputPurger(runs, index, storage).PurgeAsync(Now);

        Assert.Equal(1, outcome.Purged);
        Assert.Single(outcome.Failed);
        Assert.Contains(good.Id, runs.Stamped);
    }

    [Fact]
    public async Task TestAnAlreadyPurgedRunIsNotRevisited()
    {
        FakeRunStore runs = new();
        RunRecord done = Aged(90);
        done.InputsPurgedAt = Now.AddDays(-10);
        runs.Runs.Add(done);

        InputPurger.PurgeOutcome outcome = await new InputPurger(runs, new FakeRunFileStore(), new FakeFileStore())
            .PurgeAsync(Now);

        Assert.Equal(0, outcome.Purged);
    }
}
