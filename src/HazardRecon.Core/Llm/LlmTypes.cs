namespace HazardRecon.Core.Llm;

public class LlmModel
{
    public string Id { get; set; } = string.Empty;
    public int Provider { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
}

public class LlmMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    public LlmMessage() { }

    public LlmMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }
}

public class LlmChatResult
{
    public string Content { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
