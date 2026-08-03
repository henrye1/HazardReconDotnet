using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HazardRecon.Core.Services;

/// <summary>
/// Fingerprints a CSV's column shape, independent of filename, so a saved
/// mapping can be recognized again on a future upload of the same export
/// format. Headered files hash their (lowercased, ordered) header list;
/// headerless files hash a per-column value-shape classification instead,
/// since there is nothing else stable to key off.
/// </summary>
public static class ColumnSignature
{
    public static string Compute(IReadOnlyList<string>? headers, IReadOnlyList<IReadOnlyList<string>> sampleRows)
    {
        string canonical = headers != null
            ? "headers:" + string.Join("|", headers.Select(h => h.Trim().ToLowerInvariant()))
            : "shapes:" + string.Join("|", ShapesOf(sampleRows));

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IEnumerable<string> ShapesOf(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0) yield break;

        int cols = rows[0].Count;
        for (int c = 0; c < cols; c++)
        {
            int colIndex = c;
            yield return ClassifyColumn(rows.Where(r => colIndex < r.Count).Select(r => r[colIndex]));
        }
    }

    private static string ClassifyColumn(IEnumerable<string> values)
    {
        List<string> present = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (present.Count == 0) return "text";

        if (present.All(v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
        {
            return "date";
        }

        if (present.All(v => double.TryParse(v.Replace(" ", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out _)))
        {
            return "numeric";
        }

        return "text";
    }
}
