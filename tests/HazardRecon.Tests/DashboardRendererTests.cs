using System.Text.RegularExpressions;
using HazardRecon.Core.Exporters;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

/// <summary>
/// The dashboard is the finance-facing deliverable, so the sections it promises
/// have to actually be in the rendered HTML. Fixture data gives known values:
/// migrations 2026-01 = 2 rows, 2026-02 = 4 rows (A7 excluded, blank
/// NextBucketRating), hazard matrix rows all [0,0,0,0,.25,.75], one LGD term
/// point (Lifetime, 0 days, 0.9).
/// </summary>
public class DashboardRendererTests : IClassFixture<SyntheticDataFixture>
{
    private const string SetKey = "JUN2026 0.5PCT";
    private readonly string _html;

    public DashboardRendererTests(SyntheticDataFixture fixture)
    {
        ReconciliationEngine engine = new();
        ReconciliationRunResult run = engine.Run(fixture.RootDir, fixture.OutDir, analyze: false);
        _html = DashboardRenderer.RenderDashboard(run.Results);
    }

    [Fact]
    public void TestHasInteractiveMigrationMatrix()
    {
        Assert.Contains("Bucket migration matrix", _html);

        // a period selector wired to the render function
        Match sel = Regex.Match(_html, @"<select id='sel_(?<slug>[A-Za-z0-9_]+)'[^>]*onchange=""renderHeat\('\k<slug>'\)""");
        Assert.True(sel.Success, "expected a <select id='sel_SLUG'> wired to renderHeat('SLUG')");

        // every period plus the all-months total is offered
        Assert.Contains("<option>All months</option>", _html);
        Assert.Contains("<option>2026-01</option>", _html);
        Assert.Contains("<option>2026-02</option>", _html);

        // counts / row-% toggle
        Assert.Contains("Counts", _html);
        Assert.Contains("Row&nbsp;%", _html);

        // the client-side renderer and its data
        Assert.Contains("<script>", _html);
        Assert.Contains("function renderHeat", _html);
        Assert.Contains("function setMode", _html);
        Assert.Contains("function initHeat", _html);

        string slug = sel.Groups["slug"].Value;
        Assert.Contains($"\"{slug}\"", _html);
        // all-months matrix: 1->1 twice, 1->2 once
        Assert.Contains("[2,1,0,0,0,0]", _html.Replace(" ", ""));
    }

    [Fact]
    public void TestHasMonthlyAccountMovements()
    {
        Assert.Contains("Monthly account movements", _html);
        Assert.Matches(@"2026-01\s*</td>\s*<td[^>]*>\s*2\s*</td>", _html);
        Assert.Matches(@"2026-02\s*</td>\s*<td[^>]*>\s*4\s*</td>", _html);
    }

    [Fact]
    public void TestHasEngineHazardRateMatrix()
    {
        Assert.Contains("Engine hazard-rate matrix", _html);
        for (int i = 1; i <= 6; i++)
        {
            Assert.Contains($"From {i}", _html);
            Assert.Contains($"To {i}", _html);
        }
        // fixture hazard rows are [0,0,0,0,0.25,0.75]
        Assert.Contains("25.00%", _html);
        Assert.Contains("75.00%", _html);
    }

    [Fact]
    public void TestHasLgdTermStructure()
    {
        Assert.Contains("LGD term structure", _html);
        Assert.Contains("Lifetime", _html);
        Assert.Contains("0&nbsp;days", _html);
        Assert.Contains("90.00%", _html);
    }

    [Fact]
    public void TestDomIdsAreSafeForSetKeysContainingSpacesAndDots()
    {
        // "JUN2026 0.5PCT" must not leak a space or dot into an id/JS argument
        Assert.Contains(SetKey, _html);            // still shown to the reader
        foreach (Match m in Regex.Matches(_html, @"id='(sel|heat|c|p)_([^']*)'"))
        {
            Assert.Matches(@"^[A-Za-z0-9_]+$", m.Groups[2].Value);
        }
        Assert.DoesNotContain("renderHeat('JUN2026 0.5PCT')", _html);
    }

    [Fact]
    public void TestHasInWindowBucketConcentrationTable()
    {
        Assert.Contains("Last bucket seen", _html);
        // explains why a bucket-4 concentration matters
        Assert.Contains("worst non-default bucket", _html);

        // fixture has exactly one IN WINDOW exception: A4, last seen at bucket 4, R400
        Assert.Matches(
            @"<td>Bucket 4</td>\s*<td class='num'>1</td>\s*<td class='num'>100\.0%</td>\s*<td class='num'>R 400\.00</td>",
            _html);
    }

    [Fact]
    public void TestBucketConcentrationOrdersByAccountCountDescending()
    {
        List<WriteOffNotDefaultRecord> woNd = new()
        {
            Bkt("4", 100), Bkt("2", 10), Bkt("4", 100), Bkt("4", 100), Bkt("2", 10), Bkt("1", 5),
        };
        Dictionary<string, SingleSetResult> results = new()
        {
            ["S"] = new SingleSetResult
            {
                WoNd = woNd,
                Summary = new ReconciliationSummary { Label = "s", WoInWindow = woNd.Count }
            }
        };

        string html = DashboardRenderer.RenderDashboard(results);

        // 3x bucket 4, 2x bucket 2, 1x bucket 1 -> that order, shares over 6 in-window rows
        Assert.Matches(@"<td>Bucket 4</td>\s*<td class='num'>3</td>\s*<td class='num'>50\.0%</td>\s*<td class='num'>R 300\.00</td>", html);
        Assert.Matches(@"<td>Bucket 2</td>\s*<td class='num'>2</td>\s*<td class='num'>33\.3%</td>\s*<td class='num'>R 20\.00</td>", html);
        Assert.Matches(@"<td>Bucket 1</td>\s*<td class='num'>1</td>\s*<td class='num'>16\.7%</td>\s*<td class='num'>R 5\.00</td>", html);

        int p4 = html.IndexOf("<td>Bucket 4</td>", StringComparison.Ordinal);
        int p2 = html.IndexOf("<td>Bucket 2</td>", StringComparison.Ordinal);
        int p1 = html.IndexOf("<td>Bucket 1</td>", StringComparison.Ordinal);
        Assert.True(p4 < p2 && p2 < p1, $"rows should be ordered by count desc; got 4@{p4} 2@{p2} 1@{p1}");
    }

    [Fact]
    public void TestBucketConcentrationOmittedWhenNoLastBucketKnown()
    {
        // in-window exceptions exist, but the engine never recorded a last bucket
        List<WriteOffNotDefaultRecord> woNd = new() { Bkt(null, 50), Bkt("   ", 50) };
        Dictionary<string, SingleSetResult> results = new()
        {
            ["S"] = new SingleSetResult
            {
                WoNd = woNd,
                Summary = new ReconciliationSummary { Label = "s", WoInWindow = woNd.Count }
            }
        };

        string html = DashboardRenderer.RenderDashboard(results);
        Assert.DoesNotContain("Last bucket seen", html);
    }

    private static WriteOffNotDefaultRecord Bkt(string? lastBucket, double amount) => new()
    {
        AccountNumber = "A" + Guid.NewGuid().ToString("N")[..4],
        WriteOffVsScoringWindow = "IN WINDOW",
        LastBucketRating = lastBucket,
        WriteOffAmount = amount,
        LastWriteOffDate = new DateTime(2026, 3, 31)
    };

    [Fact]
    public void TestStillRendersWhenEngineAndMigrationsAreMissing()
    {
        Dictionary<string, SingleSetResult> bare = new()
        {
            ["EMPTY"] = new SingleSetResult { Summary = new ReconciliationSummary { Label = "no data" } }
        };

        string html = DashboardRenderer.RenderDashboard(bare);

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("No PD-scored movements available.", html);
        Assert.Contains("scenario.json not found for this set.", html);
    }
}
