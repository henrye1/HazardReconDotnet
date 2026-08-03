namespace HazardRecon.Core.Models;

/// <summary>
/// Resolves a field name to where it actually lives in a CSV: a header name if
/// the file has one, or a stringified 0-based column index if it does not. A
/// field with no entry resolves to its own name - today's literal-header-name
/// behavior, which is what a caller passing no map at all gets by default.
/// </summary>
public class ColumnMap
{
    public bool HasHeaders { get; }
    private readonly IReadOnlyDictionary<string, string> _sourceColumns;

    public ColumnMap(bool hasHeaders, IReadOnlyDictionary<string, string> sourceColumns)
    {
        HasHeaders = hasHeaders;
        _sourceColumns = sourceColumns;
    }

    public string Resolve(string field) =>
        _sourceColumns.TryGetValue(field, out string? column) ? column : field;
}

/// <summary>The two mappable files for one set - either may be null (no mapping confirmed, or the CLI path).</summary>
public record SetColumnMaps(ColumnMap? WriteOff, ColumnMap? Exposure);
