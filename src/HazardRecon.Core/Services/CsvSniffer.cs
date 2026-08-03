using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace HazardRecon.Core.Services;

/// <summary>The header row (if any) and a handful of data rows, read without assuming either shape up front.</summary>
public record CsvSniff(bool HasHeaders, IReadOnlyList<string>? Headers, IReadOnlyList<IReadOnlyList<string>> SampleRows);

/// <summary>
/// Reads just enough of a CSV to support column mapping: whether it has a
/// header row, and a few data rows to show as samples or hand to the AI
/// guesser. Never reads the whole file - write-off exports run 150k+ rows.
/// </summary>
public static class CsvSniffer
{
    public static CsvSniff Sniff(string path, int sampleRowCount = 5)
    {
        CsvConfiguration config = new(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null
        };

        using StreamReader reader = new(path);
        using CsvReader csv = new(reader, config);

        List<string[]> rawRows = new();
        while (rawRows.Count < sampleRowCount + 1 && csv.Read())
        {
            rawRows.Add(csv.Parser.Record ?? Array.Empty<string>());
        }

        if (rawRows.Count == 0)
        {
            return new CsvSniff(false, null, new List<IReadOnlyList<string>>());
        }

        bool hasHeaders = rawRows.Count > 1 && LooksLikeHeader(rawRows[0], rawRows[1]);

        List<IReadOnlyList<string>> samples = (hasHeaders ? rawRows.Skip(1) : rawRows)
            .Take(sampleRowCount)
            .Select(r => (IReadOnlyList<string>)r)
            .ToList();

        List<string>? headers = hasHeaders ? rawRows[0].ToList() : null;

        return new CsvSniff(hasHeaders, headers, samples);
    }

    /// <summary>The first row looks like a header if, for most columns, its value fails as data while the next row's does not.</summary>
    private static bool LooksLikeHeader(string[] firstRow, string[] secondRow)
    {
        int cols = Math.Min(firstRow.Length, secondRow.Length);
        if (cols == 0) return false;

        int headerLike = 0;
        for (int i = 0; i < cols; i++)
        {
            if (!LooksLikeData(firstRow[i]) && LooksLikeData(secondRow[i])) headerLike++;
        }

        return headerLike > cols / 2;
    }

    private static bool LooksLikeData(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return false;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return true;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return true;
        return false;
    }
}
