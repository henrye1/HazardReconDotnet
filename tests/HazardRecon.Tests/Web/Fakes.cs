using HazardRecon.Web.Files;
using HazardRecon.Web.Runs;

namespace HazardRecon.Tests.Web;

/// <summary>In-memory object storage, in the shape of FakeLlmClient.</summary>
public class FakeFileStore : IFileStore
{
    public Dictionary<string, byte[]> Objects { get; } = new();
    public List<string> DeletedPrefixes { get; } = new();

    /// <summary>Storage paths containing this fragment throw on upload.</summary>
    public string? FailUploadsContaining { get; set; }

    public Task UploadAsync(string storagePath, Stream content, string contentType, CancellationToken ct = default)
    {
        if (FailUploadsContaining != null && storagePath.Contains(FailUploadsContaining))
        {
            throw new IOException($"upload refused for {storagePath}");
        }

        using MemoryStream buffer = new();
        content.CopyTo(buffer);
        Objects[storagePath] = buffer.ToArray();
        return Task.CompletedTask;
    }

    public Task<string> CreateSignedUrlAsync(string storagePath, int expiresInSeconds, CancellationToken ct = default) =>
        Task.FromResult($"https://storage.example/{storagePath}?token=signed&exp={expiresInSeconds}");

    public List<string> DeletedPaths { get; } = new();

    /// <summary>Storage paths containing this fragment throw on delete.</summary>
    public string? FailDeletesContaining { get; set; }

    public Task DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        if (FailDeletesContaining != null && prefix.Contains(FailDeletesContaining))
        {
            throw new IOException($"delete refused for {prefix}");
        }

        DeletedPrefixes.Add(prefix);
        foreach (string key in Objects.Keys.Where(k => k.StartsWith(prefix)).ToList())
        {
            Objects.Remove(key);
        }
        return Task.CompletedTask;
    }

    public Task DeletePathsAsync(IReadOnlyList<string> storagePaths, CancellationToken ct = default)
    {
        foreach (string path in storagePaths)
        {
            if (FailDeletesContaining != null && path.Contains(FailDeletesContaining))
            {
                throw new IOException($"delete refused for {path}");
            }

            DeletedPaths.Add(path);
            Objects.Remove(path);
        }
        return Task.CompletedTask;
    }
}

public class FakeRunStore : IRunStore
{
    public List<RunRecord> Runs { get; } = new();
    public int RecentCount { get; set; }

    /// <summary>What the last SaveCompletionAsync call was handed, for assertions.</summary>
    public List<RunSetResultRecord> SavedSetResults { get; } = new();
    public List<LogEntryRecord> SavedLog { get; } = new();
    public List<RunOutputFileRecord> SavedOutputFiles { get; } = new();
    public List<RunCommentaryLineRecord> SavedCommentaryLines { get; } = new();
    public RunResultsRecord? SavedRunResults { get; private set; }

    public Task<RunRecord> CreateAsync(Guid userId, IReadOnlyList<string> setLabels, CancellationToken ct = default)
    {
        RunRecord run = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StatusId = RunStatus.IdOf(RunStatus.Ready),
            SetLabels = setLabels.ToList(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        Runs.Add(run);
        return Task.FromResult(run);
    }

    public Task<RunRecord?> GetAsync(Guid runId, Guid userId, CancellationToken ct = default) =>
        Task.FromResult(Runs.FirstOrDefault(r => r.Id == runId && r.UserId == userId));

    public Task<IReadOnlyList<RunRecord>> ListAsync(Guid userId, int limit = 50, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RunRecord>>(
            Runs.Where(r => r.UserId == userId).OrderByDescending(r => r.CreatedAt).Take(limit).ToList());

    public Task UpdateStatusAsync(Guid runId, string status, string? error, CancellationToken ct = default)
    {
        RunRecord? run = Runs.FirstOrDefault(r => r.Id == runId);
        if (run != null) { run.StatusId = RunStatus.IdOf(status); run.Error = error; }
        return Task.CompletedTask;
    }

    public Task SetModelAsync(Guid runId, string? modelId, CancellationToken ct = default)
    {
        RunRecord? run = Runs.FirstOrDefault(r => r.Id == runId);
        if (run != null) run.ModelId = modelId;
        return Task.CompletedTask;
    }

    public Task SaveCompletionAsync(
        Guid runId,
        Guid userId,
        string status,
        string? error,
        RunResultsRecord runResults,
        IReadOnlyList<RunSetResultRecord> setResults,
        IReadOnlyList<LogEntryRecord> log,
        IReadOnlyList<RunOutputFileRecord> outputFiles,
        IReadOnlyList<RunCommentaryLineRecord> commentaryLines,
        CancellationToken ct = default)
    {
        RunRecord? run = Runs.FirstOrDefault(r => r.Id == runId);
        if (run != null)
        {
            run.StatusId = RunStatus.IdOf(status);
            run.Error = error;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.Results = runResults;
            run.RunSetResults = setResults.ToList();
            run.Logs = log.ToList();
            run.OutputFiles = outputFiles.ToList();
            run.CommentaryLines = commentaryLines.ToList();
        }

        SavedRunResults = runResults;
        SavedSetResults.Clear();
        SavedSetResults.AddRange(setResults);
        SavedLog.Clear();
        SavedLog.AddRange(log);
        SavedOutputFiles.Clear();
        SavedOutputFiles.AddRange(outputFiles);
        SavedCommentaryLines.Clear();
        SavedCommentaryLines.AddRange(commentaryLines);

        return Task.CompletedTask;
    }

    /// <summary>Makes DeleteAsync throw, to exercise a failed delete.</summary>
    public Guid? FailDeleteFor { get; set; }

    public Task DeleteAsync(Guid runId, Guid userId, CancellationToken ct = default)
    {
        if (FailDeleteFor == runId) throw new InvalidOperationException("delete refused");

        // the real table cascades to every child, and RunRecord carries its
        // children inline, so dropping the row drops them here too
        Runs.RemoveAll(r => r.Id == runId && r.UserId == userId);
        return Task.CompletedTask;
    }

    public Task<int> CountSinceAsync(Guid userId, DateTimeOffset since, CancellationToken ct = default) =>
        Task.FromResult(RecentCount);

    public Task<int> MarkRunningAsInterruptedAsync(CancellationToken ct = default)
    {
        int n = Runs.Count(r => r.Status == RunStatus.Running);
        Runs.Where(r => r.Status == RunStatus.Running).ToList()
            .ForEach(r => r.StatusId = RunStatus.IdOf(RunStatus.Interrupted));
        return Task.FromResult(n);
    }

    public List<Guid> Stamped { get; } = new();

    /// <summary>Makes MarkInputsPurgedAsync throw for one run, to exercise retry.</summary>
    public Guid? FailStampFor { get; set; }

    public Task<IReadOnlyList<RunRecord>> ListWithUnpurgedInputsAsync(
        DateTimeOffset createdBefore, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RunRecord>>(
            Runs.Where(r => r.CreatedAt < createdBefore && r.InputsPurgedAt == null).ToList());

    public Task MarkInputsPurgedAsync(Guid runId, CancellationToken ct = default)
    {
        if (FailStampFor == runId) throw new InvalidOperationException("stamp refused");

        Stamped.Add(runId);
        RunRecord? run = Runs.FirstOrDefault(r => r.Id == runId);
        if (run != null) run.InputsPurgedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }
}

/// <summary>Records every question and answer, in order.</summary>
public class FakeChatStore : IChatStore
{
    public List<ChatMessageRecord> Messages { get; } = new();
    public bool FailAdd { get; set; }

    public Task AddAsync(IReadOnlyList<ChatMessageRecord> messages, CancellationToken ct = default)
    {
        if (FailAdd) throw new InvalidOperationException("chat store unavailable");
        Messages.AddRange(messages);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessageRecord>> ListAsync(Guid runId, Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ChatMessageRecord>>(
            Messages.Where(m => m.RunId == runId && m.UserId == userId).ToList());
}

public class FakeRunFileStore : IRunFileStore
{
    public List<RunFileRecord> Files { get; } = new();
    public bool FailAdd { get; set; }

    public Task AddAsync(IReadOnlyList<RunFileRecord> files, CancellationToken ct = default)
    {
        if (FailAdd) throw new InvalidOperationException("index unavailable");
        Files.AddRange(files);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunFileRecord>> ListAsync(Guid runId, Guid userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RunFileRecord>>(
            Files.Where(f => f.RunId == runId && f.UserId == userId).ToList());

    public Task<RunFileRecord?> FindOutputAsync(Guid runId, Guid userId, string fileName, CancellationToken ct = default) =>
        Task.FromResult(Files.FirstOrDefault(f =>
            f.RunId == runId && f.UserId == userId && f.Kind == "output" && f.RelativePath == fileName));

    public Task DeleteInputsAsync(Guid runId, CancellationToken ct = default)
    {
        Files.RemoveAll(f => f.RunId == runId && f.Kind == "input");
        return Task.CompletedTask;
    }
}

public class FakeColumnMappingStore : IColumnMappingStore
{
    public Dictionary<(Guid UserId, string FileKind, string ColumnSignature), Dictionary<string, string>> Saved { get; } = new();
    public List<(Guid RunId, string SetKey, string FileKind, IReadOnlyDictionary<string, string> Mapping)> RunMappings { get; } = new();

    public Task<IReadOnlyDictionary<string, string>> GetSavedMappingAsync(
        Guid userId, string fileKind, string columnSignature, CancellationToken ct = default)
    {
        Dictionary<string, string> mapping = Saved.TryGetValue((userId, fileKind, columnSignature), out var m)
            ? m : new Dictionary<string, string>();
        return Task.FromResult<IReadOnlyDictionary<string, string>>(mapping);
    }

    public Task SaveMappingAsync(
        Guid userId, string fileKind, string columnSignature,
        IReadOnlyDictionary<string, string> mapping, CancellationToken ct = default)
    {
        Saved[(userId, fileKind, columnSignature)] = mapping.ToDictionary(kv => kv.Key, kv => kv.Value);
        return Task.CompletedTask;
    }

    public Task RecordRunMappingAsync(
        Guid runId, string setKey, string fileKind,
        IReadOnlyDictionary<string, string> mapping, CancellationToken ct = default)
    {
        RunMappings.Add((runId, setKey, fileKind, mapping));
        return Task.CompletedTask;
    }
}
