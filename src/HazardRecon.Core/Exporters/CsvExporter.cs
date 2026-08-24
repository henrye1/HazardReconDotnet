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
        MigrationMatrixResult mig)
    {
        List<string> files = new();

        // Taken from the rows rather than from a run-type flag, so the header can
        // never disagree with what is written under it. Only a receivables book has
        // a transaction number, and there it is on every row.
        bool withTransaction = full.Any(f => f.TransactionNumber.Length > 0);

        // 1. untraced_defaults
        if (untraced.Count > 0)
        {
            string path = Path.Combine(outdir, $"{key}_untraced_defaults.csv");
            using var writer = new StreamWriter(path);
            using var csv = new CsvWriter(writer, CsvConfig);

            // Written field by field rather than through WriteHeader<T>(): adding a
            // property to the record type would put the column into the lending file
            // too, and a derived type would depend on CsvHelper's base-then-derived
            // property order.
            WriteFields(csv, "AccountNumber", withTransaction ? "TransactionNumber" : null,
                "CohortDate", "Rating", "DefaultAmount", "LastOutstanding", "RecoveredAmount", "RecoveryStatus");

            foreach (DefaultAccountRecord u in untraced.OrderByDescending(x => x.DefaultAmount))
            {
                csv.WriteField(u.AccountNumber);
                if (withTransaction) csv.WriteField(u.TransactionNumber);
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

            // For a receivables book the write-off column is replaced rather than
            // added to: its amount is per account, so there is no per-transaction
            // figure to put in it. The account total goes in its own column, written
            // once per account, so the column still sums to the real write-off.
            WriteFields(csv,
                "AccountNumber", withTransaction ? "TransactionNumber" : null,
                "CohortDate", "Rating", "DefaultAmount", "TraceSource",
                withTransaction ? "AccountWriteOffTotal" : "WriteOffAmount",
                withTransaction ? "AgeAnalysisAmount" : "IFRS9AmountOutstanding",
                "MinLgdBalance", "TraceAmount", "LossVsTraceDiff");

            HashSet<string> writeOffSeen = new();

            foreach (DefaultAccountRecord f in full)
            {
                csv.WriteField(f.AccountNumber);
                if (withTransaction) csv.WriteField(f.TransactionNumber);
                csv.WriteField(f.CohortDate);
                csv.WriteField(f.Rating);
                csv.WriteField(f.DefaultAmount);
                csv.WriteField(f.TraceSource);

                if (withTransaction)
                {
                    // once per account: repeated down every transaction, anyone
                    // totalling the column would get a multiple of the real figure
                    bool first = f.AccountWriteOffTotal.HasValue && writeOffSeen.Add(f.AccountNumber);
                    csv.WriteField(first ? f.AccountWriteOffTotal : null);
                }
                else
                {
                    csv.WriteField(f.WriteOffAmount);
                }

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

            csv.WriteRecords(woNd);
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
