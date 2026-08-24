namespace HazardRecon.Core.Models;

/// <summary>
/// Resolves a field name to where it actually lives in a CSV: a header name if
/// the file has one, or a stringified 0-based column index if it does not. A
/// field with no entry resolves to its own name - today's literal-header-name
/// behavior, which is what a caller passing no map at all gets by default.
///
/// Most fields map to exactly one column. An age analysis file's aging buckets
/// are the exception: the user picks several and they are summed, so the map is
/// backed by a list per field and <see cref="Resolve"/> hands back the first.
/// </summary>
public class ColumnMap
{
    public bool HasHeaders { get; }
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _sourceColumns;

    /// <summary>
    /// The single-column form, kept as its own constructor because that is what
    /// every caller but the age analysis passes and what the mapping endpoint
    /// still sends for a one-to-one field.
    /// </summary>
    public ColumnMap(bool hasHeaders, IReadOnlyDictionary<string, string> sourceColumns)
        : this(hasHeaders, sourceColumns.ToDictionary(
            kv => kv.Key, kv => (IReadOnlyList<string>)new[] { kv.Value }))
    {
    }

    public ColumnMap(bool hasHeaders, IReadOnlyDictionary<string, IReadOnlyList<string>> sourceColumns)
    {
        HasHeaders = hasHeaders;
        _sourceColumns = sourceColumns;
    }

    /// <summary>
    /// The column a single-valued field lives in. For a multi-valued field this is
    /// the first of them, which is meaningless on its own - use
    /// <see cref="ResolveAll"/> there.
    /// </summary>
    public string Resolve(string field) =>
        _sourceColumns.TryGetValue(field, out IReadOnlyList<string>? columns) && columns.Count > 0
            ? columns[0]
            : field;

    /// <summary>
    /// Every column mapped to a field, in the order the user picked them.
    ///
    /// A field with no entry at all resolves to its own name, matching
    /// <see cref="Resolve"/>, so a caller with no map still reads a literally-named
    /// column. An entry that is present but empty is returned empty: "the user
    /// picked nothing" and "nobody was asked" are different states, and only the
    /// caller knows whether the first is an error.
    /// </summary>
    public IReadOnlyList<string> ResolveAll(string field) =>
        _sourceColumns.TryGetValue(field, out IReadOnlyList<string>? columns)
            ? columns
            : new[] { field };
}

/// <summary>The two mappable files for one set - either may be null (no mapping confirmed, or the CLI path).</summary>
public record SetColumnMaps(ColumnMap? WriteOff, ColumnMap? Exposure);
