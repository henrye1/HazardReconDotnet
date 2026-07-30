using System.Text;
using System.Text.Json;
using HazardRecon.Web.Supabase;

namespace HazardRecon.Web.Runs;

public class SupabaseChatStore : IChatStore
{
    private const string Table = "/rest/v1/chat_messages";

    private static readonly Dictionary<string, string> ReturnRow =
        new() { ["Prefer"] = "return=representation" };

    private readonly SupabaseRestClient _rest;

    public SupabaseChatStore(SupabaseRestClient rest) => _rest = rest;

    public async Task AddAsync(IReadOnlyList<ChatMessageRecord> messages, CancellationToken ct = default)
    {
        if (messages.Count == 0) return;

        // question and answer go in one insert, so a failure cannot leave a
        // question stored with no reply beside it
        var rows = messages.Select(m => new
        {
            run_id = m.RunId,
            user_id = m.UserId,
            role = m.Role,
            content = m.Content,
            content_html = m.ContentHtml
        });

        StringContent body = new(JsonSerializer.Serialize(rows), Encoding.UTF8, "application/json");
        await _rest.SendAsync(HttpMethod.Post, Table, body, ReturnRow, ct);
    }

    public async Task<IReadOnlyList<ChatMessageRecord>> ListAsync(Guid runId, Guid userId, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{Table}?run_id=eq.{runId}&user_id=eq.{userId}&select=*&order=created_at.asc",
            null, null, ct);

        return JsonSerializer.Deserialize<List<ChatMessageRecord>>(body) ?? new List<ChatMessageRecord>();
    }
}
