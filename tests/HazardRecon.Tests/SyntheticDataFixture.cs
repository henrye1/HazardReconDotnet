using System.Text.Json;
using HazardRecon.Core.Models;

namespace HazardRecon.Tests;

public class SyntheticDataFixture : IDisposable
{
    public string RootDir { get; }
    public string OutDir { get; }
    public string WindowLo { get; } = "2025-12-01";
    public string WindowHi { get; } = "2026-06-30";

    public SyntheticDataFixture()
    {
        string temp = Path.Combine(Path.GetTempPath(), "hr_test_" + Guid.NewGuid().ToString("N")[..8]);
        RootDir = Path.Combine(temp, "data");
        OutDir = Path.Combine(temp, "out");

        string woDir = Path.Combine(RootDir, "1. WRITE-OFF FILE");
        string setDir = Path.Combine(RootDir, "3. DEBUG FILE 30 JUNE 2026 0.5 PERCENT");
        string exDir = Path.Combine(setDir, "_extracted");

        Directory.CreateDirectory(woDir);
        Directory.CreateDirectory(exDir);

        // Write-off CSV
        string woCsv = Path.Combine(woDir, "2026_WRITEOFF.csv");
        File.WriteAllText(woCsv,
            "WriteOffId,FileId,ReportDate,CustomerId,LoanAccountNumber,RepaymentDate,Amount\n" +
            "1,1,2026-03-31,C1,A1,2026-03-01,100\n" +
            "2,1,2026-03-31,C4,A4,2026-03-01,400\n" +
            "3,1,2026-07-05,C5,A5,2026-07-01,500\n" +
            "4,1,2025-06-30,C6,A6,2025-06-01,600\n");

        // IFRS9 CSV
        string ifrs9Csv = Path.Combine(setDir, "IFRS9 FILE JUNE 2026.csv");
        File.WriteAllText(ifrs9Csv,
            "Ifrs9Id,FileId,System,ReportDate,LoanType,CustomerId,LoanAccountNumber,InterestAccruedToDate,AmountOutstanding,VatOutstanding\n" +
            "1,9,T24,2026-06-30,MONTHLY,C2,A2,1,200,0\n" +
            "2,9,T24,2026-06-30,MONTHLY,C9,Z9,1,900,0\n");

        // lgd_defaults.csv
        string lgdCsv = Path.Combine(exDir, "lgd_defaults.csv");
        File.WriteAllText(lgdCsv,
            "AccountNumber,EventType,CohortDate,Bucket,Rating,Amount\n" +
            "A1,Lifetime,2026-05-31,0,5,100.0\n" +
            "A1,Lifetime,2026-05-31,1,5,100.0\n" +
            "A2,Lifetime,2026-05-31,0,5,200.0\n" +
            "A2,Lifetime,2026-05-31,1,5,200.0\n" +
            "A3,Lifetime,2026-05-31,0,5,300.0\n" +
            "A3,Lifetime,2026-05-31,1,5,300.0\n" +
            "A4,Lifetime,2026-05-31,2,4,400.0\n");

        // pd_scored.csv
        string pdCsv = Path.Combine(exDir, "pd_scored.csv");
        File.WriteAllText(pdCsv,
            "AccountNumber,Category1,ReportDate,BucketRating,NextBucketRating,DeltaLambda\n" +
            "A1,Loans,2026-01-31,1,2,0.1\n" +
            "A2,Loans,2026-01-31,2,3,0.1\n" +
            "A3,Loans,2026-02-28,3,4,0.1\n" +
            "A4,Loans,2026-02-28,4,5,0.1\n" +
            "A5,Loans,2026-02-28,1,1,0.1\n" +
            "A6,Loans,2026-02-28,1,1,0.1\n" +
            "A7,Loans,2026-02-28,6,,0.1\n");

        // debug.json
        var cohortNlambda = new List<List<double>>
        {
            new() { 2, 1, 0, 0, 0, 0 },
            new() { 0, 0, 1, 0, 0, 0 },
            new() { 0, 0, 0, 1, 0, 0 },
            new() { 0, 0, 0, 0, 1, 0 },
            new() { 0, 0, 0, 0, 0, 0 },
            new() { 0, 0, 0, 0, 0, 0 }
        };

        var debugObj = new
        {
            ScoringType = "Ageing",
            Category1 = "Loans",
            Parameters = new Dictionary<string, object>
            {
                ["MaxRating"] = 6,
                ["DefaultsRating"] = 5,
                ["PdMinDate"] = WindowLo,
                ["PdMaxDate"] = WindowHi
            },
            AccumulatedArrays = new
            {
                CohortNlambda = cohortNlambda
            }
        };
        File.WriteAllText(Path.Combine(exDir, "debug.json"), JsonSerializer.Serialize(debugObj));

        // scenario.json
        var eye = new List<List<double>>();
        for (int i = 0; i < 6; i++)
        {
            var row = new List<double> { 0.0, 0.0, 0.0, 0.0, 0.25, 0.75 };
            eye.Add(row);
        }

        var scenarioObj = new
        {
            ScoringType = "Ageing",
            Category1 = "Loans",
            HazardRateMatrix = eye,
            CohortMatrix = eye,
            Lgd = new Dictionary<string, object>
            {
                ["Lifetime"] = new
                {
                    TermStructure = new[] { new { TermDays = 0, Value = 0.9 } }
                }
            }
        };
        File.WriteAllText(Path.Combine(setDir, "scenario.json"), JsonSerializer.Serialize(scenarioObj));
    }

    public void Dispose()
    {
        try
        {
            string parent = Path.GetDirectoryName(RootDir)!;
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
        catch (Exception) { }
    }
}
