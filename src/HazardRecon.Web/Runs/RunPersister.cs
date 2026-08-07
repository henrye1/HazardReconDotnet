using HazardRecon.Web.Files;
using HazardRecon.Web.Uploads;

namespace HazardRecon.Web.Runs;

/// <summary>
/// Copies a run's files into object storage and indexes them.
///
/// Deliberately never throws at the caller: a transfer failure must not turn a
/// completed run - workbook, dashboard and CSVs already written to disk - into
/// an error. It reports what it managed, and the caller logs the rest. This is
/// the same isolation the chat payload already gets in Program.cs.
/// </summary>
public class RunPersister
{
    private readonly IFileStore _files;
    private readonly IRunFileStore _index;

    public RunPersister(IFileStore files, IRunFileStore index)
    {
        _files = files;
        _index = index;
    }

    public static string StoragePath(Guid userId, Guid runId, string kind, string relativePath) =>
        $"{userId}/{runId}/{kind}/{relativePath}";

    private static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".csv" => "text/csv; charset=utf-8",
        ".json" => "application/json",
        ".md" => "text/markdown; charset=utf-8",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream"
    };

    public record PersistOutcome(int Stored, IReadOnlyList<string> Failed);

    /// <summary>
    /// Uploads every file under <paramref name="directory"/>, preserving its
    /// structure, and records a row per file.
    /// </summary>
    public async Task<PersistOutcome> PersistDirectoryAsync(
        Guid userId,
        Guid runId,
        string kind,
        string directory,
        string? setKey = null,
        IReadOnlyDictionary<string, ReceivedFile>? describedBy = null,
        CancellationToken ct = default)
    {
        List<RunFileRecord> stored = new();
        List<string> failed = new();

        if (!Directory.Exists(directory)) return new PersistOutcome(0, failed);

        foreach (string path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            string storagePath = StoragePath(userId, runId, kind, relative);

            try
            {
                await using FileStream content = File.OpenRead(path);
                await _files.UploadAsync(storagePath, content, ContentType(relative), ct);

                ReceivedFile? described = null;
                describedBy?.TryGetValue(relative, out described);

                stored.Add(new RunFileRecord
                {
                    RunId = runId,
                    UserId = userId,
                    Kind = kind,
                    SetKey = setKey,
                    RelativePath = relative,
                    StoragePath = storagePath,
                    SizeBytes = new FileInfo(path).Length,
                    Role = described?.Role,
                    OriginalName = described?.OriginalName
                });
            }
            catch (Exception)
            {
                // recorded, not thrown: one unreachable upload must not lose the
                // rest of the run
                failed.Add(relative);
            }
        }

        try
        {
            await _index.AddAsync(stored, ct);
        }
        catch (Exception)
        {
            failed.Add($"index of {stored.Count} file(s)");
            return new PersistOutcome(0, failed);
        }

        return new PersistOutcome(stored.Count, failed);
    }
}
