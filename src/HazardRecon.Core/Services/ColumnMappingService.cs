using System.Text.Json;
using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;

namespace HazardRecon.Core.Services;

/// <summary>An AI-guessed column for one field, or none if the AI could not tell.</summary>
public record MappingGuess(string? Column, double? Confidence);

/// <summary>
/// One field resolved to a column (or not). Source is "header_match", "saved",
/// "ai_guess" or "unmapped", in the order those are tried.
/// </summary>
public record ResolvedField(string Field, string? Column, double? Confidence, string Source);

/// <summary>
/// Resolves each mappable field to a column in an uploaded file: an exact
/// header match first, then a previously saved mapping for this column
/// signature, then an AI guess from the header/sample data, then unmapped.
/// The AI call mirrors AiAnalysisService's defensive shape - any failure or
/// unparseable reply just means no guess, never blocks the caller.
/// </summary>
public class ColumnMappingService
{
    private const string SystemPrompt = @"You are matching columns in an uploaded CSV to a fixed set of
required fields for a credit-risk reconciliation tool. Given the file's header
row (if any) and a few sample rows, return ONLY a JSON object - no prose, no
markdown fences - mapping each required field name to the best-matching column
identifier (the header name if the file has headers, or a 0-based column index
as a string if it does not) and a confidence between 0 and 1. If no column
plausibly matches a field, omit that field entirely. Example shape:
{""FieldName"": {""column"": ""ColumnNameOrIndex"", ""confidence"": 0.97}}";

    private readonly ILlmClient _client;
    private readonly string _modelId;

    public ColumnMappingService(ILlmClient client, string modelId)
    {
        _client = client;
        _modelId = modelId;
    }

    public IReadOnlyList<ResolvedField> Resolve(
        IReadOnlyList<string>? headers,
        IReadOnlyList<IReadOnlyList<string>> sampleRows,
        IReadOnlyList<MappingFieldSpec> fields,
        IReadOnlyDictionary<string, string>? savedMapping,
        Action<string, string>? log = null)
    {
        List<ResolvedField> resolved = new();
        List<MappingFieldSpec> needsGuess = new();

        foreach (MappingFieldSpec field in fields)
        {
            string? exact = headers?.FirstOrDefault(h => string.Equals(h, field.Field, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                resolved.Add(new ResolvedField(field.Field, exact, null, "header_match"));
                continue;
            }

            if (savedMapping != null && savedMapping.TryGetValue(field.Field, out string? savedColumn))
            {
                resolved.Add(new ResolvedField(field.Field, savedColumn, null, "saved"));
                continue;
            }

            needsGuess.Add(field);
        }

        if (needsGuess.Count > 0)
        {
            Dictionary<string, MappingGuess> guesses = Guess(headers, sampleRows, needsGuess, log);
            foreach (MappingFieldSpec field in needsGuess)
            {
                resolved.Add(guesses.TryGetValue(field.Field, out MappingGuess? g) && g.Column != null
                    ? new ResolvedField(field.Field, g.Column, g.Confidence, "ai_guess")
                    : new ResolvedField(field.Field, null, null, "unmapped"));
            }
        }

        return resolved;
    }

    private Dictionary<string, MappingGuess> Guess(
        IReadOnlyList<string>? headers,
        IReadOnlyList<IReadOnlyList<string>> sampleRows,
        IReadOnlyList<MappingFieldSpec> fields,
        Action<string, string>? log)
    {
        Dictionary<string, MappingGuess> result = new();

        try
        {
            string columnsDescription = headers != null
                ? "Header row: " + string.Join(", ", headers)
                : "No header row. Columns are 0-based index 0.." + Math.Max(0, (sampleRows.FirstOrDefault()?.Count ?? 1) - 1) + ".";

            string samplesText = string.Join("\n", sampleRows.Take(5).Select(r => string.Join(" | ", r)));
            string fieldsText = string.Join("\n", fields.Select(f => $"- {f.Field}: {f.Note}"));

            List<LlmMessage> messages = new()
            {
                new LlmMessage("system", SystemPrompt),
                new LlmMessage("user", $"{columnsDescription}\n\nSample rows:\n{samplesText}\n\nRequired fields:\n{fieldsText}")
            };

            LlmChatResult res = _client.ChatAsync(_modelId, messages).GetAwaiter().GetResult();
            string content = (res.Content ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(content))
            {
                log?.Invoke("Column mapping: AI returned no content", LogKind.Warn);
                return result;
            }

            using JsonDocument doc = JsonDocument.Parse(content);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                string? column = prop.Value.TryGetProperty("column", out JsonElement c) ? c.GetString() : null;
                double? confidence = prop.Value.TryGetProperty("confidence", out JsonElement conf) && conf.ValueKind == JsonValueKind.Number
                    ? conf.GetDouble()
                    : null;

                if (!string.IsNullOrEmpty(column))
                {
                    result[prop.Name] = new MappingGuess(column, confidence);
                }
            }

            log?.Invoke($"Column mapping: AI guessed {result.Count} of {fields.Count} field(s)", LogKind.Ok);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Column mapping: AI unavailable: {ex.GetType().Name}: {ex.Message}", LogKind.Warn);
        }

        return result;
    }
}
