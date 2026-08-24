using CsvHelper;

namespace HazardRecon.Core.Services;

/// <summary>
/// The two refusals every CSV reader in here shares. Extracted from DataLoaders
/// so MigrationMatrixBuilder raises the identical wording for pd_scored.csv -
/// two hand-written versions of "this file has no such column" would drift, and
/// the message is what tells the user which file to fix.
/// </summary>
internal static class CsvGuards
{
    /// <summary>
    /// A column this file does not have, named before a single row is read. Missing
    /// fields are configured to return null rather than throw, so without this the
    /// account column resolving to nothing reads as a file of no accounts - which
    /// traces no defaults and reports a clean 0%, a plausible figure that is really
    /// a mapping failure. Only meaningful for a headered file; a positional map is
    /// caught by <see cref="RequireAnyAccounts"/> once the rows have been read.
    /// </summary>
    public static void RequireColumn(CsvReader csv, bool hasHeaders, string sourceColumn, string field, string path)
    {
        if (!hasHeaders || csv.HeaderRecord == null) return;
        if (csv.HeaderRecord.Contains(sourceColumn)) return;

        throw new InvalidOperationException(
            $"{Path.GetFileName(path)}: {field} is mapped to the column \"{sourceColumn}\", " +
            $"which this file does not have. Its columns are: {string.Join(", ", csv.HeaderRecord)}. " +
            "Map the columns for this file and run it again.");
    }

    /// <summary>
    /// The backstop for everything <see cref="RequireColumn"/> cannot see: a
    /// positional map pointing past the last column, or a column that is present
    /// but blank on every row. A file with rows but not one account number in it
    /// cannot reconcile anything, so it is refused rather than counted as empty.
    /// </summary>
    public static void RequireAnyAccounts(int dataRows, int accountsFound, string sourceColumn, string path)
    {
        if (dataRows == 0 || accountsFound > 0) return;

        throw new InvalidOperationException(
            $"{Path.GetFileName(path)}: none of its {dataRows:N0} rows had an account number in \"{sourceColumn}\". " +
            "Check the column mapping for this file and run it again.");
    }
}
