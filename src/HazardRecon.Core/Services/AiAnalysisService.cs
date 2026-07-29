using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HazardRecon.Core.Models;

namespace HazardRecon.Core.Services;

public class AiAnalysisService
{
    private const string Model = "claude-opus-5";
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

    public static string? GenerateAnalysis(Dictionary<string, object> payload, Action<string, string>? log = null)
    {
        string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            log?.Invoke("ANTHROPIC_API_KEY not set - skipping AI analysis", "warn");
            return null;
        }

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("x-api-key", apiKey);
            client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            string jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            string userPrompt = $"Aggregate reconciliation results:\n\n{jsonPayload}";

            var requestBody = new
            {
                model = Model,
                max_tokens = 16000,
                system = SystemPrompt,
                messages = new[]
                {
                    new { role = "user", content = userPrompt }
                }
            };

            string bodyText = JsonSerializer.Serialize(requestBody);
            using var content = new StringContent(bodyText, Encoding.UTF8, "application/json");

            HttpResponseMessage response = client.PostAsync("https://api.anthropic.com/v1/messages", content).GetAwaiter().GetResult();
            string responseText = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                log?.Invoke($"AI analysis call failed: {response.StatusCode} - {responseText}", "warn");
                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("content", out JsonElement contentArr) && contentArr.ValueKind == JsonValueKind.Array)
            {
                StringBuilder textSb = new();
                foreach (JsonElement block in contentArr.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out JsonElement t) && t.GetString() == "text" &&
                        block.TryGetProperty("text", out JsonElement txt))
                    {
                        textSb.Append(txt.GetString());
                    }
                }
                string result = textSb.ToString().Trim();
                if (!string.IsNullOrEmpty(result))
                {
                    log?.Invoke("AI analysis generated", "ok");
                    return result;
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            log?.Invoke($"AI analysis unavailable: {ex.GetType().Name}: {ex.Message}", "warn");
            return null;
        }
    }
}
