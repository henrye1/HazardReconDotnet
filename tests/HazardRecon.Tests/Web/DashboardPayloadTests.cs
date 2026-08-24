using System.Text.Json;
using System.Text.Json.Serialization;
using HazardRecon.Core.Helpers;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using HazardRecon.Web;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>
/// Built from a real run rather than a hand-made result, so the payload is checked
/// against what the engine actually produces.
/// </summary>
public class DashboardPayloadTests : IClassFixture<SyntheticDataFixture>
{
    private readonly SyntheticDataFixture _fixture;

    public DashboardPayloadTests(SyntheticDataFixture fixture) => _fixture = fixture;

    private List<DashboardSet> Run(string outDir)
    {
        ReconciliationEngine engine = new();
        ReconciliationRunResult result = engine.Run(
            _fixture.RootDir, Path.Combine(_fixture.OutDir, outDir),
            logger: (_, _) => { }, analyze: false, analyst: null);

        return result.Results.Select(kv => DashboardPayload.Build(kv.Key, kv.Value)).ToList();
    }

    [Fact]
    public void TestBothSerialisersProduceIdenticalNames()
    {
        // The bug this pins: a run is serialised twice by different code. The live
        // poll goes out through the host's response serialiser, which is camelCase,
        // while SupabaseRunStore writes the stored copy with default options, which
        // is PascalCase. Any property whose name was left to the policy therefore
        // arrived as "share" from one path and "Share" from the other, and the
        // browser - reading one spelling - crashed on the other.
        DashboardSet set = Run("dash-both")[0];

        string stored = System.Text.Json.JsonSerializer.Serialize(set);
        string live = System.Text.Json.JsonSerializer.Serialize(set,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(stored, live);
    }

    [Fact]
    public void TestNoNameIsLeftToTheNamingPolicy()
    {
        // every property, at every level, states its own wire name
        foreach (Type t in new[]
        {
            typeof(DashboardSet), typeof(LastBucketRow),
            typeof(UntracedRow), typeof(WoExceptionRow), typeof(LgdRow),
        })
        {
            foreach (var prop in t.GetProperties())
            {
                Assert.True(
                    prop.GetCustomAttributes(typeof(JsonPropertyNameAttribute), true).Length == 1,
                    $"{t.Name}.{prop.Name} has no [JsonPropertyName]; its spelling would " +
                    "then depend on which serialiser wrote it");
            }
        }
    }

    [Fact]
    public void TestTheWireNamesArePinned()
    {
        // the browser reads these keys; they are stated on the record rather than
        // left to the host's naming policy, and the rest of the result is snake_case
        string json = System.Text.Json.JsonSerializer.Serialize(Run("dash-wire")[0]);

        foreach (string key in new[]
        {
            "monthly_totals", "scored_in_writeoff", "scored_in_ifrs9", "writeoff_distinct",
            "ifrs9_distinct", "wo_pre_window", "default_pct_of_scored", "last_buckets",
            "top_untraced", "wo_exceptions",
        })
        {
            Assert.Contains("\"" + key + "\"", json);
        }

        // and the camelCase forms are absent, so nothing reads them by accident
        Assert.DoesNotContain("\"monthlyTotals\"", json);
        Assert.DoesNotContain("\"topUntraced\"", json);
    }

    [Fact]
    public void TestEverySetIsDescribed()
    {
        List<DashboardSet> sets = Run("dash-sets");

        Assert.NotEmpty(sets);
        Assert.All(sets, d => Assert.False(string.IsNullOrWhiteSpace(d.Key)));
    }

    [Fact]
    public void TestTheAggregateMonthComesFirstAndEachMonthFollows()
    {
        DashboardSet d = Run("dash-months")[0];

        Assert.Equal("All months", d.Months[0]);
        // every later entry is a period, and the matrices are keyed the same way
        Assert.All(d.Months.Skip(1), m => Assert.Matches(@"^\d{4}-\d{2}$", m));
        Assert.Equal(d.Months.OrderBy(m => m == "All months" ? "" : m), d.Months);
        Assert.Equal(d.Months.Count, d.Migration.Count);
        Assert.All(d.Months, m => Assert.True(d.Migration.ContainsKey(m), m + " has no matrix"));
    }

    [Fact]
    public void TestEveryMatrixIsSixBySix()
    {
        DashboardSet d = Run("dash-shape")[0];

        Assert.NotEmpty(d.Migration);
        foreach (var (month, rows) in d.Migration)
        {
            Assert.Equal(6, rows.Count);
            Assert.All(rows, r => Assert.Equal(6, r.Count));
            Assert.All(rows, r => Assert.All(r, v => Assert.True(v >= 0, month + " has a negative cell")));
        }
    }

    [Fact]
    public void TestTheMonthsSumToTheAggregate()
    {
        // the reference says the all-months total reconciles cell for cell
        DashboardSet d = Run("dash-sum")[0];

        List<List<int>> all = d.Migration["All months"];
        for (int i = 0; i < 6; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                int summed = d.Months.Skip(1).Sum(m => d.Migration[m][i][j]);
                Assert.Equal(all[i][j], summed);
            }
        }
    }

    [Fact]
    public void TestMonthlyTotalsLineUpWithTheMonthList()
    {
        DashboardSet d = Run("dash-monthly")[0];

        // one total per month, excluding the aggregate
        Assert.Equal(d.Months.Count - 1, d.MonthlyTotals.Count);
        for (int i = 0; i < d.MonthlyTotals.Count; i++)
        {
            int expected = d.Migration[d.Months[i + 1]].Sum(r => r.Sum());
            Assert.Equal(expected, d.MonthlyTotals[i]);
        }
    }

    [Fact]
    public void TestTheEngineMatricesComeThrough()
    {
        DashboardSet d = Run("dash-engine")[0];

        Assert.NotNull(d.Hazard);
        Assert.All(d.Hazard!, row => Assert.NotEmpty(row));
    }

    [Fact]
    public void TestLgdRowsAllShareTheSameTermColumns()
    {
        // the table has fixed columns, so every row must be the same width
        DashboardSet d = Run("dash-lgd")[0];

        if (d.Lgd.Count == 0) return;
        int width = d.Lgd[0].Values.Count;
        Assert.All(d.Lgd, r => Assert.Equal(width, r.Values.Count));
        Assert.All(d.Lgd, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
    }

    [Fact]
    public void TestTheDetailTablesAreCappedAndOrderedByAmount()
    {
        DashboardSet d = Run("dash-detail")[0];

        Assert.True(d.TopUntraced.Count <= DashboardPayload.TopUntracedRows);
        Assert.True(d.WoExceptions.Count <= DashboardPayload.TopWoExceptionRows);
        // every exception listed is one inside the scoring window
        Assert.All(d.WoExceptions, w => Assert.Equal("IN WINDOW", w.Window));

        // a lending run has no transaction to show, and the table hides the column
        // rather than printing a row of blanks
        Assert.All(d.TopUntraced, u => Assert.Equal("", u.Transaction));
    }

    /// <summary>
    /// The payload the detail screen draws from carries the transaction, or a
    /// receivables run's rows differ only by amount.
    /// </summary>
    [Fact]
    public void TestAReceivablesRunCarriesTheTransactionIntoThePayload()
    {
        SingleSetResult set = new()
        {
            Summary = new ReconciliationSummary { Label = "receivables" },
            Untraced = new List<DefaultAccountRecord>
            {
                new()
                {
                    AccountNumber = "A1", TransactionNumber = "T7",
                    AccountNormalized = AccountUtils.CompositeKey("A1", "T7"),
                    CohortDate = "2026-05-31", Rating = "5", DefaultAmount = 100
                }
            }
        };

        DashboardSet d = DashboardPayload.Build("TR", set);

        Assert.Equal("T7", Assert.Single(d.TopUntraced).Transaction);
    }

    [Fact]
    public void TestTheLastBucketSharesSumToAHundred()
    {
        DashboardSet d = Run("dash-buckets")[0];

        if (d.LastBuckets.Count == 0) return;
        Assert.Equal(100.0, d.LastBuckets.Sum(b => b.Share), 1);
        Assert.All(d.LastBuckets, b => Assert.StartsWith("Bucket ", b.Bucket));
    }

    [Fact]
    public void TestASetWithNoScoredFileYieldsNoMatrixRatherThanAnEmptyOne()
    {
        // a set missing pd_scored.csv has nothing to migrate; the month selector
        // must then have nothing to offer instead of one blank month
        DashboardSet d = DashboardPayload.Build("EMPTY", new SingleSetResult
        {
            Summary = new ReconciliationSummary(),
            Mig = new MigrationMatrixResult(),
            Engine = new EngineScenario(),
        });

        Assert.Empty(d.Months);
        Assert.Empty(d.Migration);
        Assert.Empty(d.MonthlyTotals);
        Assert.Empty(d.LastBuckets);
        Assert.Empty(d.TopUntraced);
    }
}
