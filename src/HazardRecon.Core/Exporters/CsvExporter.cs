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

        // 1. untraced_defaults
        if (untraced.Count > 0)
        {
            string path = Path.Combine(outdir, $"{key}_untraced_defaults.csv");
            using var writer = new StreamWriter(path);
            using var csv = new CsvWriter(writer, CsvConfig);

            csv.WriteHeader<UntracedCsvRecord>();
            csv.NextRecord();

            foreach (DefaultAccountRecord u in untraced.OrderByDescending(x => x.DefaultAmount))
            {
                csv.WriteRecord(new UntracedCsvRecord
                {
                    AccountNumber = u.AccountNumber,
                    CohortDate = u.CohortDate,
                    Rating = u.Rating,
                    DefaultAmount = u.DefaultAmount,
                    LastOutstanding = u.LastOutstanding,
                    RecoveredAmount = u.RecoveredAmount,
                    RecoveryStatus = u.RecoveryStatus
                });
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

            csv.WriteHeader<TracedCsvRecord>();
            csv.NextRecord();

            foreach (DefaultAccountRecord f in full)
            {
                csv.WriteRecord(new TracedCsvRecord
                {
                    AccountNumber = f.AccountNumber,
                    CohortDate = f.CohortDate,
                    Rating = f.Rating,
                    DefaultAmount = f.DefaultAmount,
                    TraceSource = f.TraceSource,
                    WriteOffAmount = f.WriteOffAmount,
                    IFRS9AmountOutstanding = f.Ifrs9AmountOutstanding,
                    MinLgdBalance = f.MinLgdBalance,
                    TraceAmount = f.TraceAmount,
                    LossVsTraceDiff = f.LossVsTraceDiff
                });
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

    private class UntracedCsvRecord
    {
        public string AccountNumber { get; set; } = string.Empty;
        public string CohortDate { get; set; } = string.Empty;
        public string Rating { get; set; } = string.Empty;
        public double DefaultAmount { get; set; }
        public double? LastOutstanding { get; set; }
        public double RecoveredAmount { get; set; }
        public string RecoveryStatus { get; set; } = string.Empty;
    }

    private class TracedCsvRecord
    {
        public string AccountNumber { get; set; } = string.Empty;
        public string CohortDate { get; set; } = string.Empty;
        public string Rating { get; set; } = string.Empty;
        public double DefaultAmount { get; set; }
        public string TraceSource { get; set; } = string.Empty;
        public double? WriteOffAmount { get; set; }
        public double? IFRS9AmountOutstanding { get; set; }
        public double MinLgdBalance { get; set; }
        public double? TraceAmount { get; set; }
        public double? LossVsTraceDiff { get; set; }
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
