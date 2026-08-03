using HazardRecon.Core.Models;

namespace HazardRecon.Web.Runs;

/// <summary>Maps LogKind's string constants to log_types' hand-seeded smallint ids.</summary>
public static class LogTypeLookup
{
    private static readonly Dictionary<string, short> Ids = new()
    {
        [LogKind.Ok] = 1,
        [LogKind.Warn] = 2,
        [LogKind.Info] = 3,
        [LogKind.Head] = 4,
        [LogKind.Tool] = 5
    };

    private static readonly Dictionary<short, string> Codes =
        Ids.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static short IdOf(string code) =>
        Ids.TryGetValue(code, out short id)
            ? id
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown log kind");

    public static string CodeOf(short id) =>
        Codes.TryGetValue(id, out string? code)
            ? code
            : throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown log type id");
}
