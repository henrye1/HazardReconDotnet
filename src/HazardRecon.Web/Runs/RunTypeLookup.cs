namespace HazardRecon.Web.Runs;

/// <summary>
/// The run type codes callers pass around as strings, mapped to the smallint ids
/// public.run_types actually stores. Same shape and rationale as RunStatus: the
/// rows are a fixed, hand-seeded enum, so the mapping is hardcoded rather than
/// looked up per call.
///
/// The type is metadata only - nothing in the engine reads it - so this class
/// exists purely to keep the id mapping in one place.
/// </summary>
public static class RunTypeLookup
{
    public const string Lending = "lending";
    public const string TradeReceivables = "trade_receivables";

    /// <summary>What a caller that says nothing gets, matching the column default.</summary>
    public const string Default = Lending;

    private static readonly Dictionary<string, short> Ids = new()
    {
        [Lending] = 1,
        [TradeReceivables] = 2
    };

    private static readonly Dictionary<short, string> Codes =
        Ids.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>
    /// Whether a code is one this application knows. Unlike a run status, a run
    /// type arrives from the browser, so an unrecognised value has to become a
    /// 400 at the edge rather than an unhandled throw out of <see cref="IdOf"/>.
    /// </summary>
    public static bool IsKnown(string? code) => code != null && Ids.ContainsKey(code);

    public static short IdOf(string code) =>
        Ids.TryGetValue(code, out short id)
            ? id
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown run type");

    public static string CodeOf(short id) =>
        Codes.TryGetValue(id, out string? code)
            ? code
            : throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown run type id");
}
