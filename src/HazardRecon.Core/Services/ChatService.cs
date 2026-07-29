using System.Text.Json;
using HazardRecon.Core.Helpers;
using HazardRecon.Core.Llm;

namespace HazardRecon.Core.Services;

/// <summary>
/// "Ask about this run". Only the run's aggregate figures are sent to the model —
/// no account-level rows leave this machine, which is why no account masking is
/// needed on this path. The trade-off is that the model can answer about totals and
/// rates but not about individual accounts.
/// </summary>
public class ChatService
{
    private const string SystemPrompt = @"You are a credit-risk analyst answering questions about one
IFRS 9 hazard-rate reconciliation run. You are given the run's aggregate results as
JSON, then a question. Answer only from those figures. Report numbers exactly as
given (thousands separators; rand amounts as R1,234,567.89). If the figures do not
contain the answer, say so plainly and say what would. You have aggregates only, not
account-level data, so you cannot answer questions about individual accounts. Keep
the answer short — a few sentences or a short bullet list. Markdown, no tables.";

    private readonly ILlmClient? _client;
    private readonly string? _modelId;

    public ChatService(ILlmClient? client, string? modelId)
    {
        _client = client;
        _modelId = modelId;
    }

    public class ChatResponse
    {
        public string Reply { get; set; } = string.Empty;
        public string ReplyHtml { get; set; } = string.Empty;
        public bool IsError { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public ChatResponse ProcessQuestion(string userQuestion, Dictionary<string, object> runAggregates)
    {
        if (string.IsNullOrEmpty(_modelId))
        {
            return new ChatResponse { IsError = true, ErrorMessage = "No model was selected for this run." };
        }

        if (_client == null)
        {
            return new ChatResponse { IsError = true, ErrorMessage = "Chat is unavailable - the LLM gateway is not configured." };
        }

        try
        {
            string json = JsonSerializer.Serialize(runAggregates, new JsonSerializerOptions { WriteIndented = true });

            List<LlmMessage> messages = new()
            {
                new LlmMessage("system", SystemPrompt),
                new LlmMessage("user", $"Reconciliation results:\n\n{json}\n\nQuestion: {userQuestion}")
            };

            LlmChatResult res = _client.ChatAsync(_modelId, messages).GetAwaiter().GetResult();
            string reply = (res.Content ?? string.Empty).Trim();

            if (reply.Length == 0)
            {
                return new ChatResponse { IsError = true, ErrorMessage = "The model returned an empty answer." };
            }

            return new ChatResponse { Reply = reply, ReplyHtml = MarkdownHelper.ToHtml(reply) };
        }
        catch (Exception ex)
        {
            return new ChatResponse { IsError = true, ErrorMessage = $"Chat is unavailable - {ex.Message}" };
        }
    }
}
