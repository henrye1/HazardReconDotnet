using System.Text.Json;

namespace HazardRecon.Web.Uploads;

/// <summary>
/// Reads the mapping-confirmation body. Lives outside the endpoint so it can be
/// tested directly: it is the one piece of request parsing here that has to cope
/// with two different JSON shapes per value, and the previous single-shape version
/// threw - a 500, not a 400 - on the other one.
/// </summary>
public static class MappingRequest
{
    /// <summary>
    /// The columns chosen for each field of one file. A value may be a single
    /// string, or an array for a field that takes several - the aging buckets of an
    /// age analysis, which are summed.
    ///
    /// Order is the user's order, because it is what the mapper card shows back.
    /// Duplicates are dropped rather than summed twice, and anything that is
    /// neither a string nor an array of strings is skipped: a malformed field is
    /// not worth failing the whole confirmation over, and an unmapped field is
    /// already a state the loaders refuse when it matters.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<string>> ReadMapping(JsonElement setElem, string fileKind)
    {
        Dictionary<string, IReadOnlyList<string>> mapping = new();

        if (!setElem.TryGetProperty(fileKind, out JsonElement fileElem) || fileElem.ValueKind != JsonValueKind.Object)
            return mapping;

        foreach (JsonProperty prop in fileElem.EnumerateObject())
        {
            List<string> columns = new();

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String:
                    string? single = prop.Value.GetString();
                    if (!string.IsNullOrEmpty(single)) columns.Add(single);
                    break;

                case JsonValueKind.Array:
                    foreach (JsonElement item in prop.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        string? column = item.GetString();
                        if (string.IsNullOrEmpty(column) || columns.Contains(column)) continue;
                        columns.Add(column);
                    }
                    break;
            }

            // An empty selection is recorded as such rather than dropped: for a
            // multi-valued field it is the difference between "the user chose no
            // buckets", which the loader refuses, and "this field was never
            // offered", which it reads literally.
            if (prop.Value.ValueKind is JsonValueKind.String or JsonValueKind.Array)
                mapping[prop.Name] = columns;
        }

        return mapping;
    }

    /// <summary>
    /// The client's "first row is a header" answer for one file, as a sibling of
    /// its mapping rather than a key inside it, so it cannot be mistaken for a
    /// field name. Null when the client said nothing, which means keep the guess.
    /// </summary>
    public static bool? ReadHasHeaders(JsonElement setElem, string fileKind) =>
        setElem.TryGetProperty(fileKind + "_has_headers", out JsonElement flag)
            && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? flag.GetBoolean()
                : null;
}
