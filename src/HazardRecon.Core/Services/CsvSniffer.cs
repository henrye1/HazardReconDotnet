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

        bool hasHeaders = rawRows.Count > 1 && LooksLikeHeader(rawRows[0], rawRows.Skip(1).ToList());

        List<IReadOnlyList<string>> samples = (hasHeaders ? rawRows.Skip(1) : rawRows)
            .Take(sampleRowCount)
            .Select(r => (IReadOnlyList<string>)r)
            .ToList();

        List<string>? headers = hasHeaders ? rawRows[0].ToList() : null;

        return new CsvSniff(hasHeaders, headers, samples);
    }

    /// <summary>
    /// The same file read as though its first row were, or were not, a header.
    ///
    /// The verdict below is a guess, and a file of nothing but words is genuinely
    /// undecidable, so the mapping step lets the user overrule it. Everything
    /// downstream then has to follow that choice rather than the guess: the column
    /// signature a saved mapping is keyed on, and the ColumnMap that tells the
    /// loaders whether to address columns by name or by position.
    ///
    /// The browser rebuilds the picker from the same two pieces the same way - see
    /// mapRowsOf and columnsFor in app.js.
    /// </summary>
    public static CsvSniff Reinterpret(CsvSniff sniff, bool hasHeaders)
    {
        if (sniff.HasHeaders == hasHeaders) return sniff;

        // back to the rows as the file has them: a sniffed header row is simply
        // the first of them
        List<IReadOnlyList<string>> rows = new();
        if (sniff.Headers != null) rows.Add(sniff.Headers);
        rows.AddRange(sniff.SampleRows);

        if (rows.Count == 0) return new CsvSniff(false, null, rows);

        return hasHeaders
            ? new CsvSniff(true, rows[0].ToList(), rows.Skip(1).ToList())
            : new CsvSniff(false, null, rows);
    }

    /// <summary>
    /// The first row is a header when nothing in it is itself a value, and the
    /// rows below it do carry data.
    ///
    /// Asked per-column and by majority, as this once was, a headered file loses
    /// its headers the moment a few labels carry digits - IFRS9_BALANCE, Q1_BAL,
    /// PD_12M, COL_1 - because the digit rule in LooksLikeData was written for
    /// data cells and misreads labels. Whether a cell is a *value* is the
    /// stricter question, and one numeric or date cell anywhere in the first row
    /// settles it: no real header row contains 44912.10 or 2026-06-30.
    /// </summary>
    private static bool LooksLikeHeader(string[] firstRow, IReadOnlyList<string[]> dataRows)
    {
        if (firstRow.Length == 0) return false;

        // a blank leading line is not a header
        if (firstRow.All(c => c.Trim().Length == 0)) return false;

        // one value in the first row and it is data, not labels
        if (firstRow.Any(LooksLikeValue)) return false;

        // and something below has to look like data, or this is a one-row file
        // of words with nothing to tell labels from values
        return dataRows.Any(row => row.Any(LooksLikeData));
    }

    /// <summary>
    /// A cell that is a value rather than a column label: it carries a digit and
    /// parses as a number or a date.
    ///
    /// The digit requirement matters because DateTime.TryParse accepts bare month
    /// names - "May", "March" - and a monthly export may well label a column
    /// that way. Requiring a digit keeps such a label from being mistaken for a
    /// date and disqualifying an otherwise obvious header row.
    /// </summary>
    private static bool LooksLikeValue(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return false;
        if (!value.Any(char.IsDigit)) return false;

        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    /// <summary>
    /// A cell that looks like data of any kind, including an identifier like
    /// "A1" or "C1" that is neither a number nor a date but does carry a digit.
    ///
    /// Only ever asked of the rows *below* the first one, as positive evidence
    /// that they are data. Asking it of the header row is what the digit rule
    /// gets wrong - see LooksLikeHeader.
    /// </summary>
    private static bool LooksLikeData(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return false;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return true;
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return true;
        if (value.Any(char.IsDigit)) return true;
        return false;
    }
}
