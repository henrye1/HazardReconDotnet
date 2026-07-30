using System.Net.Http.Headers;

namespace HazardRecon.Web.Supabase;

/// <summary>
/// Shared HTTP surface for PostgREST and Storage. Every call authenticates with
/// the service-role key, which bypasses RLS - so callers are responsible for
/// scoping requests to the authenticated user.
/// </summary>
public class SupabaseRestClient : IDisposable
{
    private readonly SupabaseOptions _options;
    private readonly HttpClient _http;

    public SupabaseRestClient(SupabaseOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
    }

    public async Task<string> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(method, _options.BaseUrl + path);
        request.Headers.TryAddWithoutValidation("apikey", _options.ServiceRoleKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServiceRoleKey);

        if (headers != null)
        {
            foreach (KeyValuePair<string, string> h in headers)
            {
                request.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
        }

        request.Content = content;

        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // body only - the request carried the service-role key and must never
            // be echoed into a log or an error surfaced to a caller
            throw new SupabaseException((int)response.StatusCode,
                $"Supabase {(int)response.StatusCode} for {method} {path}: {body}");
        }

        return body;
    }

    public void Dispose() => _http.Dispose();
}
