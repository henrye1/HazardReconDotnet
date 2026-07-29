using HazardRecon.Core.Llm;

namespace HazardRecon.Tests.Llm;

internal class FakeLlmClient : ILlmClient
{
    public List<LlmModel> Models { get; set; } = new();
    public string ReplyContent { get; set; } = "## Executive summary\n\nAll tied out.";
    public Exception? ThrowOnChat { get; set; }

    public string? LastModelId { get; private set; }
    public List<LlmMessage> LastMessages { get; private set; } = new();
    public int ChatCalls { get; private set; }

    public Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LlmModel>>(Models);

    public Task<LlmChatResult> ChatAsync(string modelId, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)
    {
        ChatCalls++;
        LastModelId = modelId;
        LastMessages = messages.ToList();

        if (ThrowOnChat != null)
        {
            return Task.FromException<LlmChatResult>(ThrowOnChat);
        }

        return Task.FromResult(new LlmChatResult { Content = ReplyContent, InputTokens = 10, OutputTokens = 20 });
    }
}
