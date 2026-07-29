namespace HazardRecon.Core.Llm;

public interface ILlmClient
{
    Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default);

    Task<LlmChatResult> ChatAsync(string modelId, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default);
}
