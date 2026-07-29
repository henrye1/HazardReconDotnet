using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using HazardRecon.Core.Helpers;
using HazardRecon.Core.Models;

namespace HazardRecon.Core.Services;

public class MigrationMatrixBuilder
{
    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        MissingFieldFound = null,
        HeaderValidated = null,
        BadDataFound = null
    };

    public MigrationMatrixResult BuildMigrationMatrix(string? pdScoredPath, HashSet<string>? detailFor = null, Action<string, string>? log = null)
    {
        MigrationMatrixResult result = new();
        detailFor ??= new HashSet<string>();

        if (string.IsNullOrEmpty(pdScoredPath) || !File.Exists(pdScoredPath))
        {
            log?.Invoke("pd_scored.csv missing - migrations and check 2 limited", "warn");
            return result;
        }

        using var reader = new StreamReader(pdScoredPath);
        using var csv = new CsvReader(reader, CsvConfig);

        csv.Read();
        csv.ReadHeader();

        while (csv.Read())
        {
            result.RowsTotal++;
            string acct = AccountUtils.NormaliseAccount(csv.GetField("AccountNumber"));
            if (!string.IsNullOrEmpty(acct))
            {
                result.ScoredAccts.Add(acct);
            }

            string reportDateStr = csv.GetField("ReportDate") ?? string.Empty;
            string fromStr = csv.GetField("BucketRating") ?? string.Empty;
            string toStr = csv.GetField("NextBucketRating") ?? string.Empty;

            bool parsedDate = DateTime.TryParse(reportDateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dts);
            bool parsedFrom = int.TryParse(fromStr, out int frm);
            bool parsedTo = int.TryParse(toStr, out int to);

            if (parsedDate && parsedFrom && parsedTo && frm >= 1 && frm <= 6 && to >= 1 && to <= 6)
            {
                result.RowsInRange++;
                var key = (dts.Year, dts.Month, frm, to);
                result.RawCounts[key] = result.RawCounts.GetValueOrDefault(key, 0) + 1;
            }

            if (detailFor.Contains(acct) && parsedDate)
            {
                if (!result.LastRating.TryGetValue(acct, out var prev) ||
                    DateTime.Parse(prev.Date, CultureInfo.InvariantCulture) < dts)
                {
                    result.LastRating[acct] = (dts.ToString("yyyy-MM-dd"), fromStr);
                }
            }
        }

        result.ScoredDistinct = result.ScoredAccts.Count;
        log?.Invoke($"migrations: {result.RowsInRange:N0}/{result.RowsTotal:N0} rows in range; {result.ScoredDistinct:N0} distinct scored accounts", "ok");

        return result;
    }

    public static int[,] MatrixForPeriod(MigrationMatrixResult mig, int? year = null, int? month = null)
    {
        int[,] mat = new int[6, 6];

        foreach (var (key, count) in mig.RawCounts)
        {
            if (year.HasValue && key.Year != year.Value) continue;
            if (month.HasValue && key.Month != month.Value) continue;

            int fIndex = key.FromBucket - 1;
            int tIndex = key.ToBucket - 1;

            if (fIndex >= 0 && fIndex < 6 && tIndex >= 0 && tIndex < 6)
            {
                mat[fIndex, tIndex] += count;
            }
        }

        return mat;
    }

    public MigrationValidationResult ReconcileMigration(MigrationMatrixResult mig, List<List<double>>? cohortNlambda)
    {
        if (cohortNlambda == null || mig == null || mig.RawCounts.Count == 0)
        {
            return new MigrationValidationResult { Status = "N/A" };
        }

        try
        {
            if (cohortNlambda.Count != 6 || cohortNlambda.Any(row => row.Count != 6))
            {
                return new MigrationValidationResult { Status = "N/A" };
            }

            int[,] ours = MatrixForPeriod(mig);
            List<(int, int, int, int)> mismatches = new();
            int maxAbs = 0;

            for (int i = 0; i < 6; i++)
            {
                for (int j = 0; j < 6; j++)
                {
                    int o = ours[i, j];
                    int e = (int)Math.Round(cohortNlambda[i][j]);
                    int d = Math.Abs(o - e);
                    if (d > 0)
                    {
                        mismatches.Add((i + 1, j + 1, o, e));
                        maxAbs = Math.Max(maxAbs, d);
                    }
                }
            }

            string status = mismatches.Count > 0 ? "FAIL" : "PASS";
            return new MigrationValidationResult
            {
                Status = status,
                MaxAbsDiff = maxAbs,
                Mismatches = mismatches
            };
        }
        catch (Exception)
        {
            return new MigrationValidationResult { Status = "N/A" };
        }
    }

    public static List<(int Year, int Month)> PeriodsOf(MigrationMatrixResult mig)
    {
        return mig.RawCounts.Keys
            .Select(k => (k.Year, k.Month))
            .Distinct()
            .OrderBy(p => p.Year)
            .ThenBy(p => p.Month)
            .ToList();
    }

    public static List<Dictionary<string, object>> BuildMonthlyFrame(MigrationMatrixResult mig)
    {
        List<Dictionary<string, object>> outFrame = new();
        if (mig.RawCounts.Count == 0) return outFrame;

        List<(string Label, int? Year, int? Month)> labelled = new()
        {
            ("All months", null, null)
        };

        foreach (var (y, mo) in PeriodsOf(mig))
        {
            labelled.Add(($"{y:D4}-{mo:D2}", y, mo));
        }

        foreach (var (label, y, mo) in labelled)
        {
            int[,] mat = MatrixForPeriod(mig, y, mo);
            for (int f = 1; f <= 6; f++)
            {
                int fIndex = f - 1;
                int rowSum = 0;
                for (int t = 1; t <= 6; t++)
                {
                    rowSum += mat[fIndex, t - 1];
                }

                Dictionary<string, object> row = new()
                {
                    ["Period"] = label,
                    ["FromBucket"] = f
                };

                for (int t = 1; t <= 6; t++)
                {
                    row[$"To_{t}"] = mat[fIndex, t - 1];
                }

                row["RowTotal"] = rowSum;

                for (int t = 1; t <= 6; t++)
                {
                    double pct = rowSum > 0 ? Math.Round((double)mat[fIndex, t - 1] / rowSum * 100.0, 2) : 0.0;
                    row[$"To_{t}_%"] = pct;
                }

                outFrame.Add(row);
            }
        }

        return outFrame;
    }
}
