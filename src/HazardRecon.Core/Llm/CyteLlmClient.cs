using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HazardRecon.Core.Llm;

/// <summary>
/// Talks to the Cyte LLM gateway. Owns the client-credentials token: it is cached
/// in memory for its stated lifetime, refreshed 5 minutes early, and refreshed once
/// more if the gateway rejects it with a 401.
/// </summary>
public class CyteLlmClient : ILlmClient, IDisposable
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly CyteLlmOptions _options;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _token;
    private DateTime _expiresAtUtc;

    public CyteLlmClient(CyteLlmOptions options, HttpMessageHandler? handler = null, Func<DateTime>? utcNow = null)
    {
        _options = options;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default)
    {
        string text = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiBaseUrl}/llm/models"), ct);

        List<LlmModel> models = new();
        using JsonDocument doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (JsonElement m in doc.RootElement.EnumerateArray())
        {
            models.Add(new LlmModel
            {
                Id = m.TryGetProperty("id", out JsonElement i) ? i.GetString() ?? string.Empty : string.Empty,
                Provider = m.TryGetProperty("provider", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                FriendlyName = m.TryGetProperty("friendlyName", out JsonElement f) ? f.GetString() ?? string.Empty : string.Empty,
                ModelName = m.TryGetProperty("modelName", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty
            });
        }

        return models;
    }

    public Task<LlmChatResult> ChatAsync(string modelId, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default) =>
        throw new NotImplementedException();

    private async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token != null && _utcNow() < _expiresAtUtc - RefreshMargin)
            {
                return _token;
            }

            var body = new
            {
                client_id = _options.ClientId,
                client_secret = _options.ClientSecret,
                audience = _options.Audience,
                grant_type = "client_credentials"
            };

            using StringContent content = new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using HttpResponseMessage res = await _http.PostAsync(_options.TokenUrl, content, ct);
            string text = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                throw new LlmException($"token request failed: {(int)res.StatusCode} {res.ReasonPhrase}");
            }

            using JsonDocument doc = JsonDocument.Parse(text);
            string? token = doc.RootElement.TryGetProperty("access_token", out JsonElement t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(token))
            {
                throw new LlmException("token response contained no access_token");
            }

            int expiresIn = doc.RootElement.TryGetProperty("expires_in", out JsonElement e) && e.ValueKind == JsonValueKind.Number
                ? e.GetInt32()
                : 3600;

            _token = token;
            _expiresAtUtc = _utcNow().AddSeconds(expiresIn);
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Sends an authenticated request. On a 401 the cached token is discarded and the
    /// request is retried exactly once with a fresh one.
    /// </summary>
    private async Task<string> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string token = await GetTokenAsync(forceRefresh: attempt > 0, ct);

            using HttpRequestMessage req = makeRequest();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage res = await _http.SendAsync(req, ct);
            string text = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                continue;
            }

            if (!res.IsSuccessStatusCode)
            {
                throw new LlmException($"{req.Method} {req.RequestUri} failed: {(int)res.StatusCode} {res.ReasonPhrase}");
            }

            return text;
        }

        throw new LlmException("authentication failed after refreshing the token");
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }
}
