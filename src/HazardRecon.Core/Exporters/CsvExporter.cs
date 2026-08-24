using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;

namespace HazardRecon.Core.Exporters;

public class CsvExporter
{
    private static readonly CsvConfiguration CsvConfig = new(CultureInfo.InvariantCulture);

    public static List<string> ExportSet(
        string outdir,
        string key,
        List<DefaultAccountRecord> untraced,
        List<DefaultAccountRecord> full,
        List<WriteOffNotDefaultRecord> woNd,
        MigrationMatrixResult mig,
        EngineRunType runType = EngineRunType.Lending)
    {
        List<string> files = new();

        // A receivables run is reconciled per customer, so every report labels its
        // identifier column accordingly rather than saying "account" over a column
        // of customer numbers.
        bool byCustomer = runType == EngineRunType.TradeReceivables;
        string idHeader = byCustomer ? "ClientNumber" : "AccountNumber";

        // 1. untraced_defaults
        if (untraced.Count > 0)
        {
            string path = Path.Combine(outdir, $"{key}_untraced_defaults.csv");
            using var writer = new StreamWriter(path);
            using var csv = new CsvWriter(writer, CsvConfig);

            // Written field by field rather than through WriteHeader<T>(), because the
            // identifier column's name depends on the run type.
            WriteFields(csv, idHeader,
                "CohortDate", "Rating", "DefaultAmount", "LastOutstanding", "RecoveredAmount", "RecoveryStatus");

            foreach (DefaultAccountRecord u in untraced.OrderByDescending(x => x.DefaultAmount))
            {
                csv.WriteField(u.AccountNumber);
                csv.WriteField(u.CohortDate);
                csv.WriteField(u.Rating);
                csv.WriteField(u.DefaultAmount);
                csv.WriteField(u.LastOutstanding);
                csv.WriteField(u.RecoveredAmount);
                csv.WriteField(u.RecoveryStatus);
                csv.NextRecord();
            }

            files.Add(Path.GetFileName(path));
        }

        // 2. traced_defaults
        if (full.Count > 0)
        {
            string path = Path.Combine(outdir, $"{key}_traced_defaults.csv");
            using var writer = new StreamWriter(path);
            using var csv = new CsvWriter(writer, CsvConfig);

            WriteFields(csv,
                idHeader, "CohortDate", "Rating", "DefaultAmount", "TraceSource", "WriteOffAmount",
                // the exposure file is an age analysis for a receivables run, so the
                // column is named for what it actually holds
                byCustomer ? "AgeAnalysisAmount" : "IFRS9AmountOutstanding",
                "MinLgdBalance", "TraceAmount", "LossVsTraceDiff");

            foreach (DefaultAccountRecord f in full)
            {
                csv.WriteField(f.AccountNumber);
                csv.WriteField(f.CohortDate);
                csv.WriteField(f.Rating);
                csv.WriteField(f.DefaultAmount);
                csv.WriteField(f.TraceSource);
                csv.WriteField(f.WriteOffAmount);
                csv.WriteField(f.Ifrs9AmountOutstanding);
                csv.WriteField(f.MinLgdBalance);
                csv.WriteField(f.TraceAmount);
                csv.WriteField(f.LossVsTraceDiff);
                csv.NextRecord();
            }

            files.Add(Path.GetFileName(path));
        }

        // 3. writeoff_not_default
        if (woNd.Count > 0)
        {
            string path = Path.Combine(outdir, $"{key}_writeoff_not_default.csv");
            using var writer = new StreamWriter(path);
            using var csv = new CsvWriter(writer, CsvConfig);

            // Not WriteRecords(woNd): its header would come from the model's property
            // order, and this file's identifier is a customer number for a
            // receivables run - the same column, differently named.
            WriteFields(csv, idHeader, "CustomerId", "WriteOffAmount", "FirstWriteOffDate",
                "LastWriteOffDate", "WriteOffVsScoringWindow", "LastScoredDate", "LastBucketRating",
                "ScoringWindow");

            foreach (WriteOffNotDefaultRecord w in woNd)
            {
                csv.WriteField(w.AccountNumber);
                csv.WriteField(w.CustomerId);
                csv.WriteField(w.WriteOffAmount);
                csv.WriteField(w.FirstWriteOffDate);
                csv.WriteField(w.LastWriteOffDate);
                csv.WriteField(w.WriteOffVsScoringWindow);
                csv.WriteField(w.LastScoredDate);
                csv.WriteField(w.LastBucketRating);
                csv.WriteField(w.ScoringWindow);
                csv.NextRecord();
            }

            files.Add(Path.GetFileName(path));
        }

        // 4. migration_matrix
        if (mig.RawCounts.Count > 0)
        {
            string path = Path.Combine(outdir, $"{key}_migration_matrix.csv");
            using var writer = new StreamWriter(path);
            using var csv = new CsvWriter(writer, CsvConfig);

            csv.WriteHeader<MigrationRawCsvRecord>();
            csv.NextRecord();

            var sortedCounts = mig.RawCounts
                .Select(kv => new MigrationRawCsvRecord
                {
                    Year = kv.Key.Year,
                    Month = kv.Key.Month,
                    FromBucket = kv.Key.FromBucket,
                    ToBucket = kv.Key.ToBucket,
                    Count = kv.Value
                })
                .OrderBy(x => x.Year)
                .ThenBy(x => x.Month)
                .ThenBy(x => x.FromBucket)
                .ThenBy(x => x.ToBucket);

            csv.WriteRecords(sortedCounts);
            files.Add(Path.GetFileName(path));
        }

        // 5. migration_monthly
        if (mig.RawCounts.Count > 0)
        {
            string path = Path.Combine(outdir, $"{key}_migration_monthly.csv");
            List<Dictionary<string, object>> frame = MigrationMatrixBuilder.BuildMonthlyFrame(mig);

            if (frame.Count > 0)
            {
                using var writer = new StreamWriter(path);
                using var csv = new CsvWriter(writer, CsvConfig);

                // Write headers dynamically
                foreach (string header in frame[0].Keys)
                {
                    csv.WriteField(header);
                }
                csv.NextRecord();

                foreach (var row in frame)
                {
                    foreach (var val in row.Values)
                    {
                        csv.WriteField(val);
                    }
                    csv.NextRecord();
                }

                files.Add(Path.GetFileName(path));
            }
        }

        return files;
    }

    /// <summary>Writes a header row, skipping any name given as null.</summary>
    private static void WriteFields(CsvWriter csv, params string?[] headers)
    {
        foreach (string? header in headers)
        {
            if (header != null) csv.WriteField(header);
        }
        csv.NextRecord();
    }

    private class MigrationRawCsvRecord
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public int FromBucket { get; set; }
        public int ToBucket { get; set; }
        public int Count { get; set; }
    }
}
