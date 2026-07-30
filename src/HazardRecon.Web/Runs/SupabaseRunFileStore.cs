using System.Text;
using System.Text.Json;
using HazardRecon.Web.Supabase;

namespace HazardRecon.Web.Runs;

public class SupabaseRunFileStore : IRunFileStore
{
    private const string Table = "/rest/v1/run_files";

    private static readonly Dictionary<string, string> ReturnRow =
        new() { ["Prefer"] = "return=representation" };

    private readonly SupabaseRestClient _rest;

    public SupabaseRunFileStore(SupabaseRestClient rest) => _rest = rest;

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static List<RunFileRecord> Parse(string body) =>
        JsonSerializer.Deserialize<List<RunFileRecord>>(body) ?? new List<RunFileRecord>();

    public async Task AddAsync(IReadOnlyList<RunFileRecord> files, CancellationToken ct = default)
    {
        if (files.Count == 0) return;

        // one insert for the whole batch - a run can produce dozens of artifacts
        var rows = files.Select(f => new
        {
            run_id = f.RunId,
            user_id = f.UserId,
            kind = f.Kind,
            set_key = f.SetKey,
            relative_path = f.RelativePath,
            storage_path = f.StoragePath,
            size_bytes = f.SizeBytes
        });

        await _rest.SendAsync(HttpMethod.Post, Table, Json(rows), ReturnRow, ct);
    }

    public async Task<IReadOnlyList<RunFileRecord>> ListAsync(Guid runId, Guid userId, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{Table}?run_id=eq.{runId}&user_id=eq.{userId}&select=*", null, null, ct);

        return Parse(body);
    }

    public async Task<RunFileRecord?> FindOutputAsync(Guid runId, Guid userId, string fileName, CancellationToken ct = default)
    {
        string encoded = Uri.EscapeDataString(fileName);
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{Table}?run_id=eq.{runId}&user_id=eq.{userId}&kind=eq.output&relative_path=eq.{encoded}&select=*",
            null, null, ct);

        return Parse(body).FirstOrDefault();
    }

    public async Task DeleteInputsAsync(Guid runId, CancellationToken ct = default)
    {
        await _rest.SendAsync(HttpMethod.Delete,
            $"{Table}?run_id=eq.{runId}&kind=eq.input", null, null, ct);
    }
}
