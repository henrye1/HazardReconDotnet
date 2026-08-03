namespace HazardRecon.Web.Runs;

/// <summary>
/// The status codes callers pass around as strings, mapped to the smallint ids
/// public.run_status actually stores. Mirrors LogKind's role for log_types: the
/// five rows are a fixed, hand-seeded enum, so the mapping is hardcoded rather
/// than looked up per call.
/// </summary>
public static class RunStatus
{
    public const string Ready = "ready";
    public const string Running = "running";
    public const string Done = "done";
    public const string Error = "error";
    public const string Interrupted = "interrupted";

    private static readonly Dictionary<string, short> Ids = new()
    {
        [Ready] = 1,
        [Running] = 2,
        [Done] = 3,
        [Error] = 4,
        [Interrupted] = 5
    };

    private static readonly Dictionary<short, string> Codes =
        Ids.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static short IdOf(string code) =>
        Ids.TryGetValue(code, out short id)
            ? id
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown run status");

    public static string CodeOf(short id) =>
        Codes.TryGetValue(id, out string? code)
            ? code
            : throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown run status id");
}
