using ClosedXML.Excel;
using HazardRecon.Core.Helpers;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;

namespace HazardRecon.Core.Exporters;

public class WorkbookExporter
{
    public static List<string> CommentaryLines(Dictionary<string, SingleSetResult> results)
    {
        List<string> lines = new();
        foreach (var (key, r) in results)
        {
            ReconciliationSummary s = r.Summary;
            int untraced = s.UntracedTotal;
            int inWindow = s.WoInWindow;
            string migStatus = s.MigValidation;

            bool clean = untraced == 0 && inWindow == 0 && (migStatus == "PASS" || migStatus == "N/A");
            lines.Add($"VERDICT ({key}): " + (clean
                ? "no exceptions - defaults, write-offs and the migration matrix all tie out."
                : "exceptions found - see the detail below before sign-off."));

            if (untraced > 0)
            {
                lines.Add($"{key}: {untraced:N0} default account(s) could not be traced to the write-off or IFRS9 file ({AccountUtils.Money(s.UntracedExposure)} exposure).");
            }

            if (inWindow > 0)
            {
                int b4 = s.WoInWindowBucket4;
                double pct4 = s.WoInWindowBucket4Pct;
                lines.Add($"{key}: {inWindow:N0} account(s) were written off inside the scoring window without ever reaching Bucket 0 ({AccountUtils.Money(s.WoInWindowAmount)}); {b4:N0} of these ({pct4:F1}%) were last seen at bucket 4, the worst non-default bucket.");
            }

            if (migStatus == "PASS")
            {
                lines.Add($"{key}: Reconciliation validated - the rebuilt migration matrix matches the engine's CohortNlambda counts cell-for-cell.");
            }
            else if (migStatus == "FAIL")
            {
                lines.Add($"{key}: the rebuilt migration matrix does NOT reconcile to the engine's CohortNlambda counts (max cell diff {s.MigValidationMaxDiff}) - investigate before relying on the migration tables.");
            }
        }
        return lines;
    }

    public static string ExportWorkbook(string outdir, Dictionary<string, SingleSetResult> results, Action<string, string>? log = null)
    {
        string filename = "hazard_rate_reconciliation.xlsx";
        string path = Path.Combine(outdir, filename);

        using var wb = new XLWorkbook();

        // 1. Summary Sheet
        IXLWorksheet wsSummary = wb.Worksheets.Add("Summary");
        wsSummary.Cell(1, 1).Value = "Debug set";
        wsSummary.Cell(1, 2).Value = "Folder";
        wsSummary.Cell(1, 3).Value = "Scoring window";
        wsSummary.Cell(1, 4).Value = "Defaults (Bucket=0)";
        wsSummary.Cell(1, 5).Value = "Default exposure";
        wsSummary.Cell(1, 6).Value = "Traced to write-off";
        wsSummary.Cell(1, 7).Value = "Traced to IFRS9";
        wsSummary.Cell(1, 8).Value = "Traced (either)";
        wsSummary.Cell(1, 9).Value = "UNTRACED defaults";
        wsSummary.Cell(1, 10).Value = "Untraced exposure";
        wsSummary.Cell(1, 11).Value = "  of which fully recovered";
        wsSummary.Cell(1, 12).Value = "Trace rate %";
        wsSummary.Cell(1, 13).Value = "Written off, never defaulted";
        wsSummary.Cell(1, 14).Value = "  of which IN WINDOW";
        wsSummary.Cell(1, 15).Value = "  in-window amount";
        wsSummary.Cell(1, 16).Value = "  in-window last at bucket 4";
        wsSummary.Cell(1, 17).Value = "  bucket-4 concentration %";
        wsSummary.Cell(1, 18).Value = "IFRS9 key overlap";
        wsSummary.Cell(1, 19).Value = "Migration matrix vs debug.json";

        int sRow = 2;
        foreach (var (key, r) in results)
        {
            ReconciliationSummary s = r.Summary;
            wsSummary.Cell(sRow, 1).Value = key;
            wsSummary.Cell(sRow, 2).Value = s.Label;
            wsSummary.Cell(sRow, 3).Value = s.Window;
            wsSummary.Cell(sRow, 4).Value = s.TotalDefaults;
            wsSummary.Cell(sRow, 5).Value = s.TotalExposure;
            wsSummary.Cell(sRow, 6).Value = s.TracedWriteOff;
            wsSummary.Cell(sRow, 7).Value = s.TracedIfrs9;
            wsSummary.Cell(sRow, 8).Value = s.TracedTotal;
            wsSummary.Cell(sRow, 9).Value = s.UntracedTotal;
            wsSummary.Cell(sRow, 10).Value = s.UntracedExposure;
            wsSummary.Cell(sRow, 11).Value = s.UntracedFullyRecovered;
            wsSummary.Cell(sRow, 12).Value = Math.Round(s.TraceRate * 100.0, 1);
            wsSummary.Cell(sRow, 13).Value = s.WoNotDefaultTotal;
            wsSummary.Cell(sRow, 14).Value = s.WoInWindow;
            wsSummary.Cell(sRow, 15).Value = s.WoInWindowAmount;
            wsSummary.Cell(sRow, 16).Value = s.WoInWindowBucket4;
            wsSummary.Cell(sRow, 17).Value = Math.Round(s.WoInWindowBucket4Pct, 1);
            wsSummary.Cell(sRow, 18).Value = s.Ifrs9KeyOverlap;
            wsSummary.Cell(sRow, 19).Value = s.MigValidation;
            sRow++;
        }

        // 2. Commentary Sheet
        IXLWorksheet wsCommentary = wb.Worksheets.Add("Commentary");
        wsCommentary.Cell(1, 1).Value = "Management commentary";
        List<string> cLines = CommentaryLines(results);
        for (int i = 0; i < cLines.Count; i++)
        {
            wsCommentary.Cell(i + 2, 1).Value = cLines[i];
        }

        // 3. Distinct accounts Sheet
        IXLWorksheet wsCensus = wb.Worksheets.Add("Distinct accounts");
        wsCensus.Cell(1, 1).Value = "Debug set";
        wsCensus.Cell(1, 2).Value = "Distinct scored accounts (pd_scored)";
        wsCensus.Cell(1, 3).Value = "Distinct defaulted accounts (Bucket=0)";
        wsCensus.Cell(1, 4).Value = "Defaults as % of scored";
        wsCensus.Cell(1, 5).Value = "Distinct write-off accounts";
        wsCensus.Cell(1, 6).Value = "Distinct IFRS9 accounts";
        wsCensus.Cell(1, 7).Value = "Scored accts also in write-off";
        wsCensus.Cell(1, 8).Value = "Scored accts also in IFRS9";

        int cRow = 2;
        foreach (var (key, r) in results)
        {
            ReconciliationSummary s = r.Summary;
            wsCensus.Cell(cRow, 1).Value = key;
            wsCensus.Cell(cRow, 2).Value = s.ScoredDistinct;
            wsCensus.Cell(cRow, 3).Value = s.DefaultsDistinct;
            wsCensus.Cell(cRow, 4).Value = s.DefaultPctOfScored.HasValue ? (XLCellValue)Math.Round(s.DefaultPctOfScored.Value * 100.0, 2) : Blank.Value;
            wsCensus.Cell(cRow, 5).Value = s.WriteOffDistinct;
            wsCensus.Cell(cRow, 6).Value = s.Ifrs9Distinct;
            wsCensus.Cell(cRow, 7).Value = s.ScoredInWriteOff;
            wsCensus.Cell(cRow, 8).Value = s.ScoredInIfrs9.HasValue ? (XLCellValue)s.ScoredInIfrs9.Value : Blank.Value;
            cRow++;
        }

        // 4. Engine PD by bucket
        IXLWorksheet wsPd = wb.Worksheets.Add("Engine PD by bucket");
        wsPd.Cell(1, 1).Value = "Debug set";
        wsPd.Cell(1, 2).Value = "Bucket";
        wsPd.Cell(1, 3).Value = "PD to default (hazard) %";
        wsPd.Cell(1, 4).Value = "PD to default (cohort) %";
        wsPd.Cell(1, 5).Value = "Prob. closed/settled (hazard) %";

        int pdRow = 2;
        foreach (var (key, r) in results)
        {
            var hz = r.Engine.HazardRateMatrix;
            var co = r.Engine.CohortMatrix;
            for (int b = 0; b < 6; b++)
            {
                wsPd.Cell(pdRow, 1).Value = key;
                wsPd.Cell(pdRow, 2).Value = b + 1;
                wsPd.Cell(pdRow, 3).Value = (hz != null && hz.Count > b && hz[b].Count > 4) ? (XLCellValue)Math.Round(hz[b][4] * 100.0, 4) : Blank.Value;
                wsPd.Cell(pdRow, 4).Value = (co != null && co.Count > b && co[b].Count > 4) ? (XLCellValue)Math.Round(co[b][4] * 100.0, 4) : Blank.Value;
                wsPd.Cell(pdRow, 5).Value = (hz != null && hz.Count > b && hz[b].Count > 5) ? (XLCellValue)Math.Round(hz[b][5] * 100.0, 4) : Blank.Value;
                pdRow++;
            }
        }

        // 5. Engine LGD term structure
        IXLWorksheet wsLgd = wb.Worksheets.Add("Engine LGD term structure");
        wsLgd.Cell(1, 1).Value = "Debug set";
        wsLgd.Cell(1, 2).Value = "EventType";
        wsLgd.Cell(1, 3).Value = "TermDays";
        wsLgd.Cell(1, 4).Value = "LGD";

        int lgdRow = 2;
        foreach (var (key, r) in results)
        {
            foreach (var (ev, ts) in r.Engine.Lgd)
            {
                foreach (var pt in ts)
                {
                    wsLgd.Cell(lgdRow, 1).Value = key;
                    wsLgd.Cell(lgdRow, 2).Value = ev;
                    wsLgd.Cell(lgdRow, 3).Value = pt.TermDays.HasValue ? (XLCellValue)pt.TermDays.Value : Blank.Value;
                    wsLgd.Cell(lgdRow, 4).Value = pt.Value.HasValue ? (XLCellValue)pt.Value.Value : Blank.Value;
                    lgdRow++;
                }
            }
        }

        // Per-set detail sheets
        foreach (var (key, r) in results)
        {
            // Untraced
            string untracedSheetName = AccountUtils.SheetName(key, "Untraced");
            IXLWorksheet wsUntraced = wb.Worksheets.Add(untracedSheetName);
            wsUntraced.Cell(1, 1).Value = "AccountNumber";
            wsUntraced.Cell(1, 2).Value = "CohortDate";
            wsUntraced.Cell(1, 3).Value = "Rating";
            wsUntraced.Cell(1, 4).Value = "DefaultAmount";
            wsUntraced.Cell(1, 5).Value = "LastOutstanding";
            wsUntraced.Cell(1, 6).Value = "RecoveredAmount";
            wsUntraced.Cell(1, 7).Value = "RecoveryStatus";

            int uRow = 2;
            foreach (DefaultAccountRecord u in r.Untraced)
            {
                wsUntraced.Cell(uRow, 1).Value = u.AccountNumber;
                wsUntraced.Cell(uRow, 2).Value = u.CohortDate;
                wsUntraced.Cell(uRow, 3).Value = u.Rating;
                wsUntraced.Cell(uRow, 4).Value = u.DefaultAmount;
                wsUntraced.Cell(uRow, 5).Value = u.LastOutstanding.HasValue ? (XLCellValue)u.LastOutstanding.Value : Blank.Value;
                wsUntraced.Cell(uRow, 6).Value = u.RecoveredAmount;
                wsUntraced.Cell(uRow, 7).Value = u.RecoveryStatus;

                if (u.RecoveryStatus == "FULLY RECOVERED")
                {
                    wsUntraced.Row(uRow).Style.Fill.BackgroundColor = XLColor.FromHtml("E6F2EC");
                    wsUntraced.Cell(uRow, 7).Style.Font.FontColor = XLColor.FromHtml("2E8B6B");
                    wsUntraced.Cell(uRow, 7).Style.Font.Bold = true;
                }
                uRow++;
            }

            // Defaults
            string defSheetName = AccountUtils.SheetName(key, "Defaults");
            IXLWorksheet wsDefaults = wb.Worksheets.Add(defSheetName);
            wsDefaults.Cell(1, 1).Value = "AccountNumber";
            wsDefaults.Cell(1, 2).Value = "CohortDate";
            wsDefaults.Cell(1, 3).Value = "Rating";
            wsDefaults.Cell(1, 4).Value = "DefaultAmount";
            wsDefaults.Cell(1, 5).Value = "TraceSource";
            wsDefaults.Cell(1, 6).Value = "WriteOffAmount";
            wsDefaults.Cell(1, 7).Value = "IFRS9AmountOutstanding";
            wsDefaults.Cell(1, 8).Value = "MinLgdBalance";
            wsDefaults.Cell(1, 9).Value = "TraceAmount";
            wsDefaults.Cell(1, 10).Value = "LossVsTraceDiff";

            int dRow = 2;
            foreach (DefaultAccountRecord f in r.Full)
            {
                wsDefaults.Cell(dRow, 1).Value = f.AccountNumber;
                wsDefaults.Cell(dRow, 2).Value = f.CohortDate;
                wsDefaults.Cell(dRow, 3).Value = f.Rating;
                wsDefaults.Cell(dRow, 4).Value = f.DefaultAmount;
                wsDefaults.Cell(dRow, 5).Value = f.TraceSource;
                wsDefaults.Cell(dRow, 6).Value = f.WriteOffAmount.HasValue ? (XLCellValue)f.WriteOffAmount.Value : Blank.Value;
                wsDefaults.Cell(dRow, 7).Value = f.Ifrs9AmountOutstanding.HasValue ? (XLCellValue)f.Ifrs9AmountOutstanding.Value : Blank.Value;
                wsDefaults.Cell(dRow, 8).Value = f.MinLgdBalance;
                wsDefaults.Cell(dRow, 9).Value = f.TraceAmount.HasValue ? (XLCellValue)f.TraceAmount.Value : Blank.Value;
                wsDefaults.Cell(dRow, 10).Value = f.LossVsTraceDiff.HasValue ? (XLCellValue)f.LossVsTraceDiff.Value : Blank.Value;
                dRow++;
            }

            // WO not default
            if (r.WoNd.Count > 0)
            {
                string woSheetName = AccountUtils.SheetName(key, "WO not dflt");
                IXLWorksheet wsWo = wb.Worksheets.Add(woSheetName);
                wsWo.Cell(1, 1).Value = "AccountNumber";
                wsWo.Cell(1, 2).Value = "CustomerId";
                wsWo.Cell(1, 3).Value = "WriteOffAmount";
                wsWo.Cell(1, 4).Value = "FirstWriteOffDate";
                wsWo.Cell(1, 5).Value = "LastWriteOffDate";
                wsWo.Cell(1, 6).Value = "WriteOffVsScoringWindow";
                wsWo.Cell(1, 7).Value = "LastScoredDate";
                wsWo.Cell(1, 8).Value = "LastBucketRating";
                wsWo.Cell(1, 9).Value = "ScoringWindow";

                int wRow = 2;
                foreach (WriteOffNotDefaultRecord w in r.WoNd)
                {
                    wsWo.Cell(wRow, 1).Value = w.AccountNumber;
                    wsWo.Cell(wRow, 2).Value = w.CustomerId;
                    wsWo.Cell(wRow, 3).Value = w.WriteOffAmount;
                    wsWo.Cell(wRow, 4).Value = w.FirstWriteOffDate.HasValue ? (XLCellValue)w.FirstWriteOffDate.Value.ToString("yyyy-MM-dd") : Blank.Value;
                    wsWo.Cell(wRow, 5).Value = w.LastWriteOffDate.HasValue ? (XLCellValue)w.LastWriteOffDate.Value.ToString("yyyy-MM-dd") : Blank.Value;
                    wsWo.Cell(wRow, 6).Value = w.WriteOffVsScoringWindow;
                    wsWo.Cell(wRow, 7).Value = w.LastScoredDate ?? "";
                    wsWo.Cell(wRow, 8).Value = w.LastBucketRating ?? "";
                    wsWo.Cell(wRow, 9).Value = w.ScoringWindow;
                    wRow++;
                }
            }

            // Migration Total & Monthly
            if (r.Mig.RawCounts.Count > 0)
            {
                int[,] totalMat = MigrationMatrixBuilder.MatrixForPeriod(r.Mig);
                string migTotSheetName = AccountUtils.SheetName(key, "MigTotal");
                IXLWorksheet wsMigTot = wb.Worksheets.Add(migTotSheetName);
                wsMigTot.Cell(1, 1).Value = "From \\ To";
                for (int t = 1; t <= 6; t++) wsMigTot.Cell(1, t + 1).Value = $"To_{t}";

                for (int f = 1; f <= 6; f++)
                {
                    wsMigTot.Cell(f + 1, 1).Value = $"From_{f}";
                    for (int t = 1; t <= 6; t++)
                    {
                        wsMigTot.Cell(f + 1, t + 1).Value = totalMat[f - 1, t - 1];
                    }
                }

                List<Dictionary<string, object>> monthlyFrame = MigrationMatrixBuilder.BuildMonthlyFrame(r.Mig);
                if (monthlyFrame.Count > 0)
                {
                    string migMonthSheetName = AccountUtils.SheetName(key, "MigMonthly");
                    IXLWorksheet wsMigMonth = wb.Worksheets.Add(migMonthSheetName);

                    int colIdx = 1;
                    foreach (string header in monthlyFrame[0].Keys)
                    {
                        wsMigMonth.Cell(1, colIdx++).Value = header;
                    }

                    int mRow = 2;
                    foreach (var mData in monthlyFrame)
                    {
                        colIdx = 1;
                        foreach (var val in mData.Values)
                        {
                            wsMigMonth.Cell(mRow, colIdx++).Value = val switch
                            {
                                int i => (XLCellValue)i,
                                double d => (XLCellValue)d,
                                _ => (XLCellValue)val.ToString()!
                            };
                        }
                        mRow++;
                    }
                }
            }

            // Engine Hazard Matrix
            if (r.Engine.HazardRateMatrix != null && r.Engine.HazardRateMatrix.Count > 0)
            {
                string hzSheetName = AccountUtils.SheetName(key, "HazardMatrix");
                IXLWorksheet wsHz = wb.Worksheets.Add(hzSheetName);
                wsHz.Cell(1, 1).Value = "From \\ To";
                for (int t = 1; t <= 6; t++) wsHz.Cell(1, t + 1).Value = $"To_{t}";

                for (int f = 1; f <= 6; f++)
                {
                    wsHz.Cell(f + 1, 1).Value = $"From_{f}";
                    for (int t = 1; t <= 6; t++)
                    {
                        double hzVal = (r.Engine.HazardRateMatrix.Count > f - 1 && r.Engine.HazardRateMatrix[f - 1].Count > t - 1)
                            ? r.Engine.HazardRateMatrix[f - 1][t - 1]
                            : 0.0;
                        wsHz.Cell(f + 1, t + 1).Value = hzVal;
                    }
                }
            }
        }

        wb.SaveAs(path);
        log?.Invoke($"workbook written: {filename}", "ok");
        return filename;
    }
}
