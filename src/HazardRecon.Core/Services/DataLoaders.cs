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

    private static CsvConfiguration ConfigFor(bool hasHeaders) => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = hasHeaders,
        MissingFieldFound = null,
        HeaderValidated = null,
        BadDataFound = null
    };

    private static string? Field(CsvReader csv, bool hasHeaders, string sourceColumn) =>
        hasHeaders
            ? csv.GetField(sourceColumn)
            : (int.TryParse(sourceColumn, out int idx) ? csv.GetField(idx) : csv.GetField(sourceColumn));


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

    /// <param name="runType">
    /// Trade receivables keys a default on (account, client) rather than on the
    /// account alone, because one account holds many transactions and each is a
    /// debt in its own right.
    /// </param>
    public List<DefaultAccountRecord> LoadDefaults(
        string lgdPath, Action<string, string>? log = null,
        EngineRunType runType = EngineRunType.Lending)
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

        // A receivables book is reconciled per customer, so the customer number is
        // the identifier - not an extra one alongside the account.
        bool byCustomer = runType == EngineRunType.TradeReceivables;
        string idColumn = byCustomer ? "ClientNumber" : "AccountNumber";

        // Refused before a row is read, and refused rather than degraded: this
        // config returns null for a column the file does not have, so without the
        // guard every key would come out empty and match nothing - a 0% trace rate
        // that looks like a finding rather than a malformed export.
        if (byCustomer)
            CsvGuards.RequireColumn(csv, true, idColumn, "the client number", lgdPath);

        List<RawLgdRow> allRows = new();
        while (csv.Read())
        {
            string rawAcct = csv.GetField(idColumn) ?? string.Empty;
            string acct = AccountUtils.NormaliseAccount(rawAcct);
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

        // MinLgdBalance per join key across all rows. Summed for receivables for the
        // same reason the default amount is: a customer's minimum-across-one-invoice
        // is not comparable with an exposure that covers all of them.
        Dictionary<string, double> minLgd = allRows
            .Where(r => r.Amount.HasValue)
            .GroupBy(r => r.AccountNormalized)
            .ToDictionary(
                g => g.Key,
                g => byCustomer
                    ? g.GroupBy(r => r.RawAccountNumber).Sum(a => a.Min(r => r.Amount!.Value))
                    : g.Min(r => r.Amount!.Value));

        // Post-default trajectory: prefer Lifetime event type
        List<RawLgdRow> traj = allRows.Where(r => string.Equals(r.EventType, "Lifetime", StringComparison.OrdinalIgnoreCase)).ToList();
        if (traj.Count == 0) traj = allRows;

        // LastObservation & LastOutstanding per join key (highest BucketN)
        var lastObs = traj
            .Where(r => r.BucketN.HasValue)
            .GroupBy(r => r.AccountNormalized)
            .ToDictionary(g => g.Key, g => {
                var lastRow = g.OrderBy(r => r.BucketN!.Value).Last();
                return (LastObsBucket: lastRow.BucketN!.Value, LastOutstanding: lastRow.Amount);
            });

        // Filter Bucket == 0 defaults.
        //
        // This grouping IS the definition of "a default": one record per distinct
        // join key. For lending that key is the account, and the largest Bucket-0
        // amount wins - one account's rows are restatements of one debt.
        //
        // A customer, though, can hold several genuinely separate defaulted
        // invoices, so taking the largest would silently discard the rest and
        // understate the exposure. For receivables they are summed instead, and the
        // descriptive fields come from the largest contributor.
        var bucket0Groups = allRows
            .Where(r => r.Bucket == "0")
            .GroupBy(r => r.AccountNormalized)
            .Select(g => new
            {
                Largest = g.OrderByDescending(r => r.Amount ?? 0.0).First(),
                Total = byCustomer ? g.Sum(r => r.Amount ?? 0.0) : (double?)null
            })
            .ToList();

        List<DefaultAccountRecord> defaults = new();
        foreach (var group in bucket0Groups)
        {
            RawLgdRow d0 = group.Largest;
            string acct = d0.AccountNormalized;
            double defAmt = group.Total ?? d0.Amount ?? 0.0;
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
        log?.Invoke($"defaults: {defaults.Count:N0} distinct {runType.Noun()} at Bucket 0 (exposure {AccountUtils.Money(totalExp)}); {fullyRecoveredCount:N0} fully recovered post-default", LogKind.Ok);

        return defaults;
    }

    /// <param name="runType">
    /// Receivables join on the customer number, because that is what the defaults
    /// and the age analysis are keyed on - the account number is carried instead.
    /// </param>
    public (List<WriteOffAggRecord> AggRecords, HashSet<string> AccountSet) LoadWriteoff(
        string? path, Action<string, string>? log = null, ColumnMap? columnMap = null,
        EngineRunType runType = EngineRunType.Lending)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            log?.Invoke("write-off file MISSING - check 2 cannot run", LogKind.Warn);
            return (new List<WriteOffAggRecord>(), new HashSet<string>());
        }

        bool hasHeaders = columnMap?.HasHeaders ?? true;
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, ConfigFor(hasHeaders));

        if (hasHeaders)
        {
            csv.Read();
            csv.ReadHeader();
        }

        string accountCol = columnMap?.Resolve("LoanAccountNumber") ?? "LoanAccountNumber";
        string customerCol = columnMap?.Resolve("CustomerId") ?? "CustomerId";
        string amtCol = columnMap?.Resolve("Amount") ?? "Amount";
        string dateCol = columnMap?.Resolve("ReportDate") ?? "ReportDate";

        // The two identifiers swap roles by run type: whichever one the defaults are
        // keyed on is the join key here, and the other is carried through.
        bool byCustomer = runType == EngineRunType.TradeReceivables;
        string keyCol = byCustomer ? customerCol : accountCol;
        string carriedCol = byCustomer ? accountCol : customerCol;
        string keyWhat = byCustomer ? "the write-off customer number" : "the write-off account number";

        // the join key is not optional: check 1 traces every default through it
        CsvGuards.RequireColumn(csv, hasHeaders, keyCol, keyWhat, path);

        // the rest each cost one figure rather than the whole check, so they are
        // reported and carried on with: no date limits check 2's window, no amount
        // leaves the write-off totals at zero
        foreach ((string col, string what) in new[]
        {
            (carriedCol, byCustomer ? "account number" : "customer id"),
            (amtCol, "write-off amount"),
            (dateCol, "report date"),
        })
        {
            if (hasHeaders && csv.HeaderRecord != null && !csv.HeaderRecord.Contains(col))
                log?.Invoke($"write-off: no \"{col}\" column for the {what} - that figure will be empty", LogKind.Warn);
        }

        List<RawWriteOffRow> rawRows = new();
        int dataRows = 0;
        while (csv.Read())
        {
            dataRows++;
            // normalised whichever column it comes from: a customer number arrives
            // from the same float-mangling exports and carries the same trailing ".0",
            // so reading it raw would stop it matching the defaults file
            string acct = AccountUtils.NormaliseAccount(Field(csv, hasHeaders, keyCol));
            if (string.IsNullOrEmpty(acct)) continue;

            string custId = Field(csv, hasHeaders, carriedCol) ?? string.Empty;
            double amt = double.TryParse(Field(csv, hasHeaders, amtCol), NumberStyles.Any, CultureInfo.InvariantCulture, out double val) ? val : 0.0;
            DateTime? reportDate = DateTime.TryParse(Field(csv, hasHeaders, dateCol), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt) ? dt : null;

            rawRows.Add(new RawWriteOffRow
            {
                AccountNormalized = acct,
                CustomerId = custId,
                Amount = amt,
                ReportDate = reportDate
            });
        }

        CsvGuards.RequireAnyAccounts(dataRows, rawRows.Count, keyCol, path);

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
        log?.Invoke($"write-off: {agg.Count:N0} distinct {runType.Noun()} from {rawRows.Count:N0} rows{dateRangeStr}", LogKind.Ok);

        return (agg, acctSet);
    }

    public SourceAccountsResult LoadSourceAccounts(
        string? path, string colName, string label, string? amountCol = null,
        Action<string, string>? log = null, ColumnMap? columnMap = null)
    {
        SourceAccountsResult res = new();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            log?.Invoke($"{label}: file MISSING", LogKind.Warn);
            return res;
        }

        bool hasHeaders = columnMap?.HasHeaders ?? true;
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, ConfigFor(hasHeaders));

        if (hasHeaders)
        {
            csv.Read();
            csv.ReadHeader();
        }

        string resolvedColName = columnMap?.Resolve(colName) ?? colName;
        string? resolvedAmountCol = amountCol == null ? null : (columnMap?.Resolve(amountCol) ?? amountCol);

        bool hasAmountCol = resolvedAmountCol != null &&
            (!hasHeaders || (csv.HeaderRecord != null && csv.HeaderRecord.Contains(resolvedAmountCol)));

        // the account column is what the defaults are traced into, so a file that
        // does not have it cannot contribute a trace and must say so
        CsvGuards.RequireColumn(csv, hasHeaders, resolvedColName, $"{label} account number", path);

        if (resolvedAmountCol != null && !hasAmountCol)
            log?.Invoke($"{label}: no \"{resolvedAmountCol}\" column - exposure per account will be empty", LogKind.Warn);

        while (csv.Read())
        {
            res.TotalRows++;
            string acct = AccountUtils.NormaliseAccount(Field(csv, hasHeaders, resolvedColName));
            if (string.IsNullOrEmpty(acct)) continue;

            res.AccountNumbers.Add(acct);

            if (hasAmountCol)
            {
                double amt = double.TryParse(Field(csv, hasHeaders, resolvedAmountCol!), NumberStyles.Any, CultureInfo.InvariantCulture, out double val) ? val : 0.0;
                res.AmountsPerAccount[acct] = res.AmountsPerAccount.GetValueOrDefault(acct, 0.0) + amt;
            }
        }

        CsvGuards.RequireAnyAccounts(res.TotalRows, res.AccountNumbers.Count, resolvedColName, path);

        log?.Invoke($"{label}: {res.AccountNumbers.Count:N0} distinct accounts", LogKind.Ok);
        return res;
    }

    /// <summary>
    /// A trade receivables age analysis: one row per transaction, with the balance
    /// spread across aging buckets. Returns the same shape as
    /// <see cref="LoadSourceAccounts"/> so everything downstream is unaware, but the
    /// key is (account, transaction) and the amount is the sum of whichever buckets
    /// the user said count as defaulted.
    ///
    /// Its own method rather than more optional parameters on LoadSourceAccounts:
    /// that one is the generic "account column and maybe one amount" reader, and
    /// this has a required second key part and N amount columns.
    /// </summary>
    public SourceAccountsResult LoadAgeAnalysis(
        string? path, string label, Action<string, string>? log = null, ColumnMap? columnMap = null)
    {
        SourceAccountsResult res = new();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            log?.Invoke($"{label}: file MISSING", LogKind.Warn);
            return res;
        }

        bool hasHeaders = columnMap?.HasHeaders ?? true;
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, ConfigFor(hasHeaders));

        if (hasHeaders)
        {
            csv.Read();
            csv.ReadHeader();
        }

        // hoisted: resolving these per row would repeat the dictionary lookups for
        // every line of what is the largest file in a set
        string idCol = columnMap?.Resolve("ClientNumber") ?? "ClientNumber";
        string[] bucketCols = (columnMap?.ResolveAll("AgingBuckets") ?? Array.Empty<string>()).ToArray();

        // Not a warning: with nothing selected every row sums to zero, check 1 still
        // "traces" every default to a zero exposure, and the run reports figures
        // that look real. There is no sensible default for which buckets mean
        // default, so refusing is the only honest answer.
        if (bucketCols.Length == 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)}: no aging bucket columns were selected, so there is no " +
                "exposure to sum. Choose at least one aging column for this file and run it again.");
        }

        // A customer number that is also summed as a bucket would silently become
        // the exposure - and these identifiers are numeric, so it would parse
        // cleanly into a large, plausible, wrong figure.
        string[] keyCols = bucketCols.Where(c => c == idCol).ToArray();
        if (keyCols.Length > 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(path)}: {string.Join(", ", keyCols)} is mapped both as the " +
                "join key and as an aging bucket. Pick different columns and run it again.");
        }

        CsvGuards.RequireColumn(csv, hasHeaders, idCol, $"{label} customer number", path);

        // every bucket, not just the first: one the file has lost would otherwise
        // contribute zero and understate the exposure without a word
        foreach (string bucket in bucketCols)
            CsvGuards.RequireColumn(csv, hasHeaders, bucket, $"{label} aging bucket", path);

        Dictionary<string, int> unparseable = new();
        int negativeRows = 0;

        while (csv.Read())
        {
            res.TotalRows++;

            string key = AccountUtils.NormaliseAccount(Field(csv, hasHeaders, idCol));
            if (string.IsNullOrEmpty(key)) continue;

            res.AccountNumbers.Add(key);

            double rowTotal = 0.0;
            foreach (string bucket in bucketCols)
            {
                string? raw = Field(csv, hasHeaders, bucket);
                if (string.IsNullOrWhiteSpace(raw)) continue;

                if (TryParseAmount(raw, out double val)) rowTotal += val;
                else unparseable[bucket] = unparseable.GetValueOrDefault(bucket) + 1;
            }

            if (rowTotal < 0) negativeRows++;

            // accumulate rather than assign: an age analysis can carry more than one
            // row for the same transaction
            res.AmountsPerAccount[key] = res.AmountsPerAccount.GetValueOrDefault(key, 0.0) + rowTotal;
        }

        CsvGuards.RequireAnyAccounts(res.TotalRows, res.AccountNumbers.Count, idCol, path);

        // named per column, because summing six buckets gives six independent
        // chances for a format this reader cannot read
        foreach ((string bucket, int count) in unparseable)
            log?.Invoke($"{label}: {count:N0} row(s) had an unreadable value in \"{bucket}\" - counted as zero", LogKind.Warn);

        // a credit sitting in a selected bucket reduces the defaulted exposure, and
        // "(1,234.56)" parses as negative, so this is worth saying out loud
        if (negativeRows > 0)
            log?.Invoke($"{label}: {negativeRows:N0} row(s) summed to a negative exposure across the selected buckets", LogKind.Warn);

        log?.Invoke(
            $"{label}: {res.AccountNumbers.Count:N0} distinct customers from {res.TotalRows:N0} rows, " +
            $"summing {bucketCols.Length} aging bucket(s) ({string.Join(" + ", bucketCols)})", LogKind.Ok);

        return res;
    }

    /// <summary>
    /// Amounts arrive space-separated in this part of the world ("1 234.56"), which
    /// NumberStyles.Any does not handle - ColumnSignature already strips spaces for
    /// the same reason. Without this a whole aging column reads as zero.
    /// </summary>
    private static bool TryParseAmount(string raw, out double value) =>
        double.TryParse(
            raw.Replace(" ", "").Replace("\u00A0", ""),
            NumberStyles.Any, CultureInfo.InvariantCulture, out value);

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
