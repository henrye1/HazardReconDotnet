using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HazardRecon.Web.Supabase;

namespace HazardRecon.Web.Files;

public class SupabaseFileStore : IFileStore
{
    private readonly SupabaseRestClient _rest;
    private readonly SupabaseOptions _options;
    private readonly string _bucket;

    public SupabaseFileStore(SupabaseRestClient rest, SupabaseOptions options, string bucket = "runs")
    {
        _rest = rest;
        _options = options;
        _bucket = bucket;
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public async Task UploadAsync(string storagePath, Stream content, string contentType, CancellationToken ct = default)
    {
        StreamContent body = new(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        await _rest.SendAsync(HttpMethod.Post,
            $"/storage/v1/object/{_bucket}/{storagePath}",
            body,
            new Dictionary<string, string> { ["x-upsert"] = "true" },
            ct);
    }

    public async Task<string> CreateSignedUrlAsync(string storagePath, int expiresInSeconds, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Post,
            $"/storage/v1/object/sign/{_bucket}/{storagePath}",
            Json(new { expiresIn = expiresInSeconds }), null, ct);

        using JsonDocument doc = JsonDocument.Parse(body);
        string relative = doc.RootElement.GetProperty("signedURL").GetString()
            ?? throw new SupabaseException(500, "Sign response carried no signedURL.");

        // Supabase returns a path relative to /storage/v1
        return $"{_options.BaseUrl}/storage/v1{relative}";
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        string listBody = await _rest.SendAsync(HttpMethod.Post,
            $"/storage/v1/object/list/{_bucket}",
            Json(new { prefix, limit = 1000 }), null, ct);

        List<string> paths = new();
        using (JsonDocument doc = JsonDocument.Parse(listBody))
        {
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("name", out JsonElement name) && name.GetString() is string n)
                {
                    paths.Add($"{prefix}/{n}");
                }
            }
        }

        await DeletePathsAsync(paths, ct);
    }

    public async Task DeletePathsAsync(IReadOnlyList<string> storagePaths, CancellationToken ct = default)
    {
        if (storagePaths.Count == 0) return;

        await _rest.SendAsync(HttpMethod.Delete,
            $"/storage/v1/object/{_bucket}",
            Json(new { prefixes = storagePaths }), null, ct);
    }
}
