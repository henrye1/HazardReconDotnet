namespace HazardRecon.Web.Uploads;

/// <summary>One uploaded file, tagged with the set (folder) it belongs to.</summary>
public record UploadItem(int SetIndex, string RelativePath, Stream Content, long Length);

/// <summary>
/// Where a set landed on disk. <see cref="Root"/> is what the discoverer is
/// pointed at; <see cref="Label"/> is the folder name the user picked, which is
/// what the run is named after.
/// </summary>
public record UploadedSet(string Root, string Label, int FileCount, long Bytes);

public record UploadOutcome(bool Ok, string? Error, IReadOnlyList<UploadedSet> Sets)
{
    public static UploadOutcome Fail(string error) => new(false, error, Array.Empty<UploadedSet>());
}

/// <summary>
/// Rehydrates uploaded folders into a directory that looks exactly like the
/// folders the app used to be pointed at, so InputDiscoverer and the engine run
/// against it unchanged.
/// </summary>
public class UploadReceiver
{
    public const int MaxSets = 4;
    public const int MaxFilesPerSet = 500;

    /// <summary>
    /// Default ceiling per folder. Real debug folders carrying both debug.zip and
    /// its extracted contents run well past 150 MB, so this is deliberately
    /// generous; override with Uploads:MaxBytesPerSet where storage is tighter.
    /// </summary>
    public const long DefaultMaxBytesPerSet = 512L * 1024 * 1024;

    public long MaxBytesPerSet { get; }

    public UploadReceiver(long maxBytesPerSet = DefaultMaxBytesPerSet) =>
        MaxBytesPerSet = maxBytesPerSet;

    /// <summary>
    /// Writes every item under <paramref name="destinationRoot"/>. Each set gets
    /// its own numbered directory, so two folders that happen to share a name do
    /// not collide.
    /// </summary>
    public async Task<UploadOutcome> ReceiveAsync(
        string destinationRoot,
        IReadOnlyList<UploadItem> items,
        CancellationToken ct = default)
    {
        if (items.Count == 0)
        {
            return UploadOutcome.Fail("Please choose at least one folder.");
        }

        List<int> setIndexes = items.Select(i => i.SetIndex).Distinct().OrderBy(i => i).ToList();
        if (setIndexes.Count > MaxSets)
        {
            return UploadOutcome.Fail($"A maximum of {MaxSets} folders is supported.");
        }

        List<UploadedSet> sets = new();

        foreach (int setIndex in setIndexes)
        {
            List<UploadItem> setItems = items.Where(i => i.SetIndex == setIndex).ToList();

            if (setItems.Count > MaxFilesPerSet)
            {
                return UploadOutcome.Fail(
                    $"Folder {setIndex + 1} holds {setItems.Count} files; the limit is {MaxFilesPerSet}.");
            }

            long total = setItems.Sum(i => i.Length);
            if (total > MaxBytesPerSet)
            {
                return UploadOutcome.Fail(
                    $"Folder {setIndex + 1} is {total / (1024 * 1024)} MB; the limit is {MaxBytesPerSet / (1024 * 1024)} MB.");
            }

            string setRoot = Path.Combine(destinationRoot, setIndex.ToString());
            string? label = null;

            foreach (UploadItem item in setItems)
            {
                if (!UploadPath.TryNormalize(item.RelativePath, out string relative))
                {
                    return UploadOutcome.Fail($"Rejected an unsafe file path: {item.RelativePath}");
                }

                // the first segment is the picked folder's own name, which is what
                // the discoverer derives the set key and label from
                label ??= relative.Split('/')[0];

                string full = Path.GetFullPath(Path.Combine(setRoot, relative));

                // belt and braces: TryNormalize already refuses anything that could
                // climb out, but the check is cheap and this is the line that matters
                string setRootFull = Path.GetFullPath(setRoot);
                if (!full.StartsWith(setRootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return UploadOutcome.Fail($"Rejected an unsafe file path: {item.RelativePath}");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);

                await using FileStream file = File.Create(full);
                await item.Content.CopyToAsync(file, ct);
            }

            if (label == null)
            {
                return UploadOutcome.Fail($"Folder {setIndex + 1} had no usable files.");
            }

            sets.Add(new UploadedSet(
                Root: Path.Combine(setRoot, label),
                Label: label,
                FileCount: setItems.Count,
                Bytes: total));
        }

        return new UploadOutcome(true, null, sets);
    }
}
