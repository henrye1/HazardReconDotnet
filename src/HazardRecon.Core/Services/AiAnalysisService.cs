using System.Text.Json;
using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;

namespace HazardRecon.Core.Services;

public class AiAnalysisService
{
    private const string SystemPrompt = @"You are a senior credit-risk analyst at Anchor Point Risk writing
for a bank's finance and audit teams. You receive aggregate results of an
IFRS 9 hazard-rate reconciliation as JSON. Write a rigorous, plain-language
analysis in Markdown with exactly these sections:

## Executive summary
## Check 1 - default traceability
## Check 2 - write-offs never flagged as default
## Bucket migrations
## Data quality flags
## Recommended actions

Rules: report numbers exactly as given (thousands separators; rand amounts
as R1,234,567.89). Call out root-cause patterns the aggregates support -
e.g. a concentration of in-window exceptions by last-scored bucket. When
more than one set is present, compare them. Flag anomalies (zero IFRS9
overlap, missing files, empty windows). Never invent numbers that are not
in the input. Use headings, short paragraphs and bullet lists only - no
Markdown tables. Keep it under 700 words.";

    private readonly ILlmClient _client;
    private readonly string _modelId;

    public AiAnalysisService(ILlmClient client, string modelId)
    {
        _client = client;
        _modelId = modelId;
    }

    public static Dictionary<string, object> BuildAnalysisPayload(Dictionary<string, SingleSetResult> results)
    {
        List<Dictionary<string, object?>> sets = new();

        foreach (var (key, r) in results)
        {
            ReconciliationSummary s = r.Summary;

            Dictionary<string, int> hist = new();
            if (r.WoNd != null && r.WoNd.Count > 0)
            {
                var inw = r.WoNd.Where(w => w.WriteOffVsScoringWindow == "IN WINDOW");
                hist = inw
                    .GroupBy(w => w.LastBucketRating ?? "unknown")
                    .ToDictionary(g => g.Key, g => g.Count());
            }

            Dictionary<string, object>? matrix = null;
            if (r.Mig.RawCounts.Count > 0)
            {
                int[,] m = MigrationMatrixBuilder.MatrixForPeriod(r.Mig);
                List<List<int>> countsList = new();
                for (int i = 0; i < 6; i++)
                {
                    List<int> row = new();
                    for (int j = 0; j < 6; j++) row.Add(m[i, j]);
                    countsList.Add(row);
                }

                matrix = new Dictionary<string, object>
                {
                    ["buckets"] = new List<int> { 1, 2, 3, 4, 5, 6 },
                    ["from_to_counts"] = countsList
                };
            }

            sets.Add(new Dictionary<string, object?>
            {
                ["key"] = key,
                ["label"] = s.Label,
                ["window"] = s.Window,
                ["defaults"] = s.TotalDefaults,
                ["default_exposure"] = s.TotalExposure,
                ["traced_writeoff"] = s.TracedWriteOff,
                ["traced_ifrs9"] = s.TracedIfrs9,
                ["untraced"] = s.UntracedTotal,
                ["untraced_exposure"] = s.UntracedExposure,
                ["untraced_fully_recovered"] = s.UntracedFullyRecovered,
                ["untraced_fully_recovered_amount"] = s.UntracedFullyRecoveredAmount,
                ["trace_rate"] = s.TraceRate,
                ["check2_total"] = s.WoNotDefaultTotal,
                ["check2_in_window"] = s.WoInWindow,
                ["check2_in_window_amount"] = s.WoInWindowAmount,
                ["check2_post_window"] = s.WoPostWindow,
                ["check2_pre_window"] = s.WoPreWindow,
                ["in_window_last_bucket_hist"] = hist,
                ["scored_distinct"] = s.ScoredDistinct,
                ["writeoff_distinct"] = s.WriteOffDistinct,
                ["ifrs9_distinct"] = s.Ifrs9Distinct,
                ["ifrs9_key_overlap"] = s.Ifrs9KeyOverlap,
                ["migration_matrix"] = matrix,
                ["migration_validation"] = s.MigValidation,
                ["engine_params"] = r.Engine.Params
            });
        }

        return new Dictionary<string, object> { ["sets"] = sets };
    }

    /// <summary>
    /// Blocks on the async client because the engine is synchronous and already runs
    /// on a background thread. Returns null on any failure — a gateway outage must
    /// never fail a reconciliation.
    /// </summary>
    public string? GenerateAnalysis(Dictionary<string, object> payload, Action<string, string>? log = null)
    {
        try
        {
            string jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

            List<LlmMessage> messages = new()
            {
                new LlmMessage("system", SystemPrompt),
                new LlmMessage("user", $"Aggregate reconciliation results:\n\n{jsonPayload}")
            };

            LlmChatResult res = _client.ChatAsync(_modelId, messages).GetAwaiter().GetResult();
            string result = (res.Content ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(result))
            {
                log?.Invoke("AI analysis returned no content", "warn");
                return null;
            }

            log?.Invoke($"AI analysis generated ({res.OutputTokens:N0} output tokens)", "ok");
            return result;
        }
        catch (Exception ex)
        {
            log?.Invoke($"AI analysis unavailable: {ex.GetType().Name}: {ex.Message}", "warn");
            return null;
        }
    }
}
