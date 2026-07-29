namespace HazardRecon.Core.Llm;

/// <summary>
/// Connection settings for the Cyte LLM gateway. Bound by the host projects and
/// passed into Core, so Core needs no configuration dependency of its own.
/// </summary>
public class CyteLlmOptions
{
    public string TokenUrl { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TokenUrl) &&
        !string.IsNullOrWhiteSpace(Audience) &&
        !string.IsNullOrWhiteSpace(ApiBaseUrl) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
