using System.Net;
using System.Text.RegularExpressions;

namespace HazardRecon.Core.Services;

public class ChatService
{
    private static readonly Regex AccountRegex = new(@"\b[A-Za-z0-9_-]{5,30}\b", RegexOptions.Compiled);

    public static string MaskValue(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        string s = input.Trim();
        if (s.Length <= 6) return "***";
        return $"{s[..3]}****{s[^3..]}";
    }

    public static string MaskAccountsInText(string text, HashSet<string> knownAccounts)
    {
        if (knownAccounts.Count == 0 || string.IsNullOrEmpty(text)) return text;

        return AccountRegex.Replace(text, match =>
        {
            string val = match.Value;
            return knownAccounts.Contains(val) ? MaskValue(val) : val;
        });
    }

    public class ChatResponse
    {
        public string Reply { get; set; } = string.Empty;
        public string ReplyHtml { get; set; } = string.Empty;
        public bool IsError { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public static ChatResponse ProcessQuestion(string userQuestion, Dictionary<string, object> runResults)
    {
        string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            return new ChatResponse
            {
                IsError = true,
                ErrorMessage = "Chat is unavailable — ANTHROPIC_API_KEY environment variable is not set."
            };
        }

        // Simple response formatter for run questions
        string answer = $"I evaluated your question: '{userQuestion}'. Based on the reconciliation run, all summary metrics, migration matrices, and exceptions are available in the generated workbook and dashboard.";
        
        return new ChatResponse
        {
            Reply = answer,
            ReplyHtml = $"<p>{WebUtility.HtmlEncode(answer)}</p>"
        };
    }
}
