using System.Globalization;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using HazardRecon.Core.Helpers;
using HazardRecon.Core.Models;

namespace HazardRecon.Core.Services;

public class DataLoaders
{
    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = true,
        MissingFieldFound = null,
        HeaderValidated = null,
        BadDataFound = null
    };

    public EngineScenario LoadScenario(string? scenarioPath, string? debugJsonPath, Action<string, string>? log = null)
    {
        EngineScenario scenario = new();

        if (!string.IsNullOrEmpty(scenarioPath) && File.Exists(scenarioPath))
        {
            try
            {
                string jsonText = File.ReadAllText(scenarioPath);
                using JsonDocument doc = JsonDocument.Parse(jsonText);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("ScoringType", out JsonElement st)) scenario.ScoringType = st.GetString();
                if (root.TryGetProperty("Category1", out JsonElement cat)) scenario.Category = cat.GetString();

                if (root.TryGetProperty("HazardRateMatrix", out JsonElement hrm) && hrm.ValueKind == JsonValueKind.Array)
                {
                    scenario.HazardRateMatrix = ParseDoubleMatrix(hrm);
                }

                if (root.TryGetProperty("CohortMatrix", out JsonElement cm) && cm.ValueKind == JsonValueKind.Array)
                {
                    scenario.CohortMatrix = ParseDoubleMatrix(cm);
                }

                if (root.TryGetProperty("Lgd", out JsonElement lgdElem) && lgdElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in lgdElem.EnumerateObject())
                    {
                        List<EngineLgdTermPoint> points = new();
                        if (prop.Value.TryGetProperty("TermStructure", out JsonElement tsElem) && tsElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement pt in tsElem.EnumerateArray())
                            {
                                int? termDays = pt.TryGetProperty("TermDays", out JsonElement td) && td.ValueKind == JsonValueKind.Number ? td.GetInt32() : null;
                                double? val = pt.TryGetProperty("Value", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                                points.Add(new EngineLgdTermPoint { TermDays = termDays, Value = val });
                            }
                        }
                        scenario.Lgd[prop.Name] = points;
                    }
                }

                log?.Invoke($"scenario.json: hazard matrix {(scenario.HazardRateMatrix != null ? "ok" : "missing")}, {scenario.Lgd.Count} LGD term structure(s)", LogKind.Ok);
            }
            catch (Exception ex)
            {
                log?.Invoke($"scenario.json parse warning: {ex.Message}", LogKind.Warn);
            }
        }
        else
        {
            log?.Invoke("scenario.json not found for this set", LogKind.Warn);
        }

        if (!string.IsNullOrEmpty(debugJsonPath) && File.Exists(debugJsonPath))
        {
            try
            {
                string jsonText = File.ReadAllText(debugJsonPath);
                using JsonDocument doc = JsonDocument.Parse(jsonText);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("Parameters", out JsonElement paramsElem) && paramsElem.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in paramsElem.EnumerateObject())
                    {
                        scenario.Params[prop.Name] = prop.Value.ToString();
                    }
                }

                if (root.TryGetProperty("GeneratedAt", out JsonElement genAt))
                {
                    scenario.GeneratedAt = genAt.GetString();
                }

                if (root.TryGetProperty("AccumulatedArrays", out JsonElement aaElem) && aaElem.ValueKind == JsonValueKind.Object)
                {
                    if (aaElem.TryGetProperty("CohortNlambda", out JsonElement cnElem) && cnElem.ValueKind == JsonValueKind.Array)
                    {
                        scenario.CohortNlambda = ParseDoubleMatrix(cnElem);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"debug.json parse warning: {ex.Message}", LogKind.Warn);
            }
        }

        return scenario;
    }

    private static List<List<double>> ParseDoubleMatrix(JsonElement arrayElem)
    {
        List<List<double>> matrix = new();
        foreach (JsonElement rowElem in arrayElem.EnumerateArray())
        {
            List<double> row = new();
            if (rowElem.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement valElem in rowElem.EnumerateArray())
                {
                    if (valElem.ValueKind == JsonValueKind.Number)
                        row.Add(valElem.GetDouble());
                    else if (double.TryParse(valElem.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                        row.Add(d);
                    else
                        row.Add(0.0);
                }
            }
            matrix.Add(row);
        }
        return matrix;
    }

    public List<DefaultAccountRecord> LoadDefaults(string lgdPath, Action<string, string>? log = null)
    {
        if (!File.Exists(lgdPath))
        {
            log?.Invoke("lgd_defaults.csv MISSING", LogKind.Warn);
            return new List<DefaultAccountRecord>();
        }

        using var reader = new StreamReader(lgdPath);
        using var csv = new CsvReader(reader, CsvConfig);

        csv.Read();
        csv.ReadHeader();

        List<RawLgdRow> allRows = new();
        while (csv.Read())
        {
            string acct = AccountUtils.NormaliseAccount(csv.GetField("AccountNumber"));
            string rawAcct = csv.GetField("AccountNumber") ?? string.Empty;
            string bucket = (csv.GetField("Bucket") ?? string.Empty).Trim();
            string eventType = (csv.GetField("EventType") ?? string.Empty).Trim();
            string cohortDate = csv.GetField("CohortDate") ?? string.Empty;
            string rating = csv.GetField("Rating") ?? string.Empty;

            double? amt = double.TryParse(csv.GetField("Amount"), NumberStyles.Any, CultureInfo.InvariantCulture, out double val) ? val : null;
            double? bktN = double.TryParse(bucket, NumberStyles.Any, CultureInfo.InvariantCulture, out double bn) ? bn : null;

            allRows.Add(new RawLgdRow
            {
                RawAccountNumber = rawAcct,
                AccountNormalized = acct,
                Bucket = bucket,
                BucketN = bktN,
                EventType = eventType,
                CohortDate = cohortDate,
                Rating = rating,
                Amount = amt
            });
        }

        // MinLgdBalance per account across all rows
        Dictionary<string, double> minLgd = allRows
            .Where(r => r.Amount.HasValue)
            .GroupBy(r => r.AccountNormalized)
            .ToDictionary(g => g.Key, g => g.Min(r => r.Amount!.Value));

        // Post-default trajectory: prefer Lifetime event type
        List<RawLgdRow> traj = allRows.Where(r => string.Equals(r.EventType, "Lifetime", StringComparison.OrdinalIgnoreCase)).ToList();
        if (traj.Count == 0) traj = allRows;

        // LastObservation & LastOutstanding per account (highest BucketN)
        var lastObs = traj
            .Where(r => r.BucketN.HasValue)
            .GroupBy(r => r.AccountNormalized)
            .ToDictionary(g => g.Key, g => {
                var lastRow = g.OrderBy(r => r.BucketN!.Value).Last();
                return (LastObsBucket: lastRow.BucketN!.Value, LastOutstanding: lastRow.Amount);
            });

        // Filter Bucket == 0 defaults
        var bucket0Rows = allRows
            .Where(r => r.Bucket == "0")
            .GroupBy(r => r.AccountNormalized)
            .Select(g => g.OrderByDescending(r => r.Amount ?? 0.0).First())
            .ToList();

        List<DefaultAccountRecord> defaults = new();
        foreach (RawLgdRow d0 in bucket0Rows)
        {
            string acct = d0.AccountNormalized;
            double defAmt = d0.Amount ?? 0.0;
            double minBal = minLgd.TryGetValue(acct, out double mb) ? mb : defAmt;

            double? lastBucket = null;
            double? lastOut = null;
            if (lastObs.TryGetValue(acct, out var lo))
            {
                lastBucket = lo.LastObsBucket;
                lastOut = lo.LastOutstanding;
            }

            double recAmt = defAmt - (lastOut ?? 0.0);
            string status;
            if (!lastBucket.HasValue || lastBucket.Value == 0)
            {
                status = "NO POST-DEFAULT DATA";
            }
            else if (lastOut.HasValue && lastOut.Value == 0)
            {
                status = "FULLY RECOVERED";
            }
            else if (lastOut.HasValue && lastOut.Value < defAmt)
            {
                status = "PARTIAL RECOVERY";
            }
            else
            {
                status = "NO RECOVERY OBSERVED";
            }

            defaults.Add(new DefaultAccountRecord
            {
                AccountNumber = d0.RawAccountNumber,
                AccountNormalized = acct,
                CohortDate = d0.CohortDate,
                Rating = d0.Rating,
                DefaultAmount = defAmt,
                MinLgdBalance = minBal,
                LastObsBucket = lastBucket,
                LastOutstanding = lastOut,
                RecoveredAmount = recAmt,
                RecoveryStatus = status
            });
        }

        int fullyRecoveredCount = defaults.Count(d => d.RecoveryStatus == "FULLY RECOVERED");
        double totalExp = defaults.Sum(d => d.DefaultAmount);
        log?.Invoke($"defaults: {defaults.Count:N0} distinct accounts at Bucket 0 (exposure {AccountUtils.Money(totalExp)}); {fullyRecoveredCount:N0} fully recovered post-default", LogKind.Ok);

        return defaults;
    }

    public (List<WriteOffAggRecord> AggRecords, HashSet<string> AccountSet) LoadWriteoff(string? path, Action<string, string>? log = null)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            log?.Invoke("write-off file MISSING - check 2 cannot run", LogKind.Warn);
            return (new List<WriteOffAggRecord>(), new HashSet<string>());
        }

        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CsvConfig);

        csv.Read();
        csv.ReadHeader();

        List<RawWriteOffRow> rawRows = new();
        while (csv.Read())
        {
            string acct = AccountUtils.NormaliseAccount(csv.GetField("LoanAccountNumber"));
            if (string.IsNullOrEmpty(acct)) continue;

            string custId = csv.GetField("CustomerId") ?? string.Empty;
            double amt = double.TryParse(csv.GetField("Amount"), NumberStyles.Any, CultureInfo.InvariantCulture, out double val) ? val : 0.0;
            DateTime? reportDate = DateTime.TryParse(csv.GetField("ReportDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt) ? dt : null;

            rawRows.Add(new RawWriteOffRow
            {
                AccountNormalized = acct,
                CustomerId = custId,
                Amount = amt,
                ReportDate = reportDate
            });
        }

        List<WriteOffAggRecord> agg = rawRows
            .GroupBy(r => r.AccountNormalized)
            .Select(g => new WriteOffAggRecord
            {
                AccountNormalized = g.Key,
                CustomerId = g.First().CustomerId,
                WriteOffAmount = g.Sum(r => r.Amount),
                FirstWriteOffDate = g.Where(r => r.ReportDate.HasValue).Min(r => r.ReportDate),
                LastWriteOffDate = g.Where(r => r.ReportDate.HasValue).Max(r => r.ReportDate),
                WriteOffRows = g.Count()
            })
            .ToList();

        HashSet<string> acctSet = agg.Select(a => a.AccountNormalized).ToHashSet();

        DateTime? minDate = rawRows.Where(r => r.ReportDate.HasValue).Min(r => r.ReportDate);
        DateTime? maxDate = rawRows.Where(r => r.ReportDate.HasValue).Max(r => r.ReportDate);

        string dateRangeStr = (minDate.HasValue && maxDate.HasValue) ? $" ({minDate.Value:yyyy-MM-dd} to {maxDate.Value:yyyy-MM-dd})" : "";
        log?.Invoke($"write-off: {agg.Count:N0} distinct accounts from {rawRows.Count:N0} rows{dateRangeStr}", LogKind.Ok);

        return (agg, acctSet);
    }

    public SourceAccountsResult LoadSourceAccounts(string? path, string colName, string label, string? amountCol = null, Action<string, string>? log = null)
    {
        SourceAccountsResult res = new();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            log?.Invoke($"{label}: file MISSING", LogKind.Warn);
            return res;
        }

        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CsvConfig);

        csv.Read();
        csv.ReadHeader();

        bool hasAmountCol = !string.IsNullOrEmpty(amountCol) && csv.HeaderRecord != null && csv.HeaderRecord.Contains(amountCol);

        while (csv.Read())
        {
            res.TotalRows++;
            string acct = AccountUtils.NormaliseAccount(csv.GetField(colName));
            if (string.IsNullOrEmpty(acct)) continue;

            res.AccountNumbers.Add(acct);

            if (hasAmountCol)
            {
                double amt = double.TryParse(csv.GetField(amountCol), NumberStyles.Any, CultureInfo.InvariantCulture, out double val) ? val : 0.0;
                res.AmountsPerAccount[acct] = res.AmountsPerAccount.GetValueOrDefault(acct, 0.0) + amt;
            }
        }

        log?.Invoke($"{label}: {res.AccountNumbers.Count:N0} distinct accounts", LogKind.Ok);
        return res;
    }

    private class RawLgdRow
    {
        public string RawAccountNumber { get; set; } = string.Empty;
        public string AccountNormalized { get; set; } = string.Empty;
        public string Bucket { get; set; } = string.Empty;
        public double? BucketN { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string CohortDate { get; set; } = string.Empty;
        public string Rating { get; set; } = string.Empty;
        public double? Amount { get; set; }
    }

    private class RawWriteOffRow
    {
        public string AccountNormalized { get; set; } = string.Empty;
        public string CustomerId { get; set; } = string.Empty;
        public double Amount { get; set; }
        public DateTime? ReportDate { get; set; }
    }
}
