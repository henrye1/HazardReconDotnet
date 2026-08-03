namespace HazardRecon.Web.Uploads;

public enum SetFileKind { Exposure, Writeoff, Debug, Scenario }

/// <summary>One uploaded file, tagged with the set and role the client picked it for.</summary>
public record SetFileItem(int SetIndex, SetFileKind Kind, string OriginalFileName, Stream Content, long Length);

/// <summary>Where a set landed on disk, and the original names of its two mappable files.</summary>
public record ReceivedSet(string Root, string Label, string ExposureFileName, string WriteOffFileName, int FileCount, long Bytes);

public record SetReceiveOutcome(bool Ok, string? Error, IReadOnlyList<ReceivedSet> Sets)
{
    public static SetReceiveOutcome Fail(string error) => new(false, error, Array.Empty<ReceivedSet>());
}

/// <summary>
/// Writes each set's four uploaded files under the canonical name
/// InputDiscoverer.BuildSet already looks for (IFRS9.csv, writeoff.csv,
/// scenario.json), so discovery needs no changes even though the client no
/// longer sends a folder tree - only debug-kind files keep their own names,
/// since lgd_defaults.csv/pd_scored.csv/debug.json/debug.zip are fixed names
/// from the source system, not something a bank renames.
/// </summary>
public class SetFileReceiver
{
    public const int MaxSets = 4;
    public const long DefaultMaxBytesPerSet = 512L * 1024 * 1024;

    public long MaxBytesPerSet { get; }

    public SetFileReceiver(long maxBytesPerSet = DefaultMaxBytesPerSet) => MaxBytesPerSet = maxBytesPerSet;

    public async Task<SetReceiveOutcome> ReceiveAsync(
        string destinationRoot, IReadOnlyList<SetFileItem> items, CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            return SetReceiveOutcome.Fail("Please choose at least one set's files.");
        }

        List<int> setIndexes = items.Select(i => i.SetIndex).Distinct().OrderBy(i => i).ToList();
        if (setIndexes.Count > MaxSets)
        {
            return SetReceiveOutcome.Fail($"A maximum of {MaxSets} sets is supported.");
        }

        List<ReceivedSet> sets = new();

        foreach (int setIndex in setIndexes)
        {
            List<SetFileItem> setItems = items.Where(i => i.SetIndex == setIndex).ToList();

            long total = setItems.Sum(i => i.Length);
            if (total > MaxBytesPerSet)
            {
                return SetReceiveOutcome.Fail(
                    $"Set {setIndex + 1} is {total / (1024 * 1024)} MB; the limit is {MaxBytesPerSet / (1024 * 1024)} MB.");
            }

            string? exposureName = setItems.FirstOrDefault(i => i.Kind == SetFileKind.Exposure)?.OriginalFileName;
            string? writeoffName = setItems.FirstOrDefault(i => i.Kind == SetFileKind.Writeoff)?.OriginalFileName;

            if (exposureName == null || writeoffName == null)
            {
                return SetReceiveOutcome.Fail($"Set {setIndex + 1} is missing its exposure or write-off file.");
            }

            string setRoot = Path.Combine(destinationRoot, setIndex.ToString());
            Directory.CreateDirectory(setRoot);

            foreach (SetFileItem item in setItems)
            {
                string destName = item.Kind switch
                {
                    SetFileKind.Exposure => "IFRS9.csv",
                    SetFileKind.Writeoff => "writeoff.csv",
                    SetFileKind.Scenario => "scenario.json",
                    SetFileKind.Debug => Path.GetFileName(item.OriginalFileName),
                    _ => throw new ArgumentOutOfRangeException(nameof(items), item.Kind, "Unknown file kind")
                };

                string full = Path.Combine(setRoot, destName);
                await using FileStream file = File.Create(full);
                await item.Content.CopyToAsync(file, ct);
            }

            string label = Path.GetFileNameWithoutExtension(exposureName);
            sets.Add(new ReceivedSet(setRoot, label, exposureName, writeoffName, setItems.Count, total));
        }

        return new SetReceiveOutcome(true, null, sets);
    }
}
