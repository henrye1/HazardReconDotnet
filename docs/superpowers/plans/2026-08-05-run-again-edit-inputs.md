# Run again: edit a past run's inputs — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** "Run again" opens the Files step showing the run's stored inputs, so any one file can be replaced and the rest are reused server-side without being re-uploaded.

**Architecture:** Input files are already persisted to object storage and indexed in `run_files`; this exposes them through a read endpoint, and lets `/api/discover` rebuild a set from a previous run's objects. Reused files are downloaded into a temp directory and handed to `SetFileReceiver` as ordinary `SetFileItem`s, so canonical naming, the exposure requirement, per-set size limits and set labelling all work unchanged. The client's slot arrays gain a second occupant type — a stored-file descriptor — which renders identically to a picked `File`.

**Tech Stack:** .NET 10 minimal APIs, xUnit + `WebApplicationFactory`, Supabase (PostgREST + Storage), vanilla JS front end with a bespoke Node harness (`tests/client/app.harness.mjs`).

## Global Constraints

- Design doc: `docs/superpowers/specs/2026-08-05-run-again-edit-inputs-design.md`. Read it before starting.
- Migrations must be **re-runnable** — this schema has been applied by hand as well as by the CLI. Guard every statement; drop-then-create every policy. Follow `supabase/migrations/20260803000000_column_mappings.sql`.
- Input retention stays 30 days (`InputPurger.RetentionWindow`). Do not change it.
- Every read of a run or its files is scoped by `user_id`. An unknown run and another user's run must be indistinguishable — return **404, never 403**, matching `/api/run` (`Program.cs:471`).
- `HazardRecon.Core` and `HazardRecon.Cli` must not change. No test in `tests/HazardRecon.Tests` outside `Web/` should need editing.
- Role values are exactly `exposure`, `writeoff`, `debug`, `scenario` — lowercase, matching `SetFileKind` names.
- Input `relative_path` values are relative to the run's `input` directory and look like `0/IFRS9.csv` — the leading segment is the **set index**.
- Run both suites before every commit: `dotnet test tests/HazardRecon.Tests` and `node tests/client/app.harness.mjs`.
- If a `dotnet` build fails with MSB3027 "file is locked by Microsoft Visual Studio", the dev web app is running — stop it, or use `--no-build` when only JS changed.

---

### Task 1: `run_files` records each input's role and original name

`SetFileReceiver` renames exposure/write-off/scenario files to canonical names, so `relative_path` cannot say which file the user picked. Persist both the original name and the role.

**Files:**
- Create: `supabase/migrations/20260805000000_run_file_roles.sql`
- Modify: `src/HazardRecon.Web/Runs/RunFileRecord.cs`
- Test: `tests/HazardRecon.Tests/Web/RunFileRecordTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `RunFileRecord.OriginalName` (`string?`, json `original_name`) and `RunFileRecord.Role` (`string?`, json `role`).

- [ ] **Step 1: Write the failing test**

Create `tests/HazardRecon.Tests/Web/RunFileRecordTests.cs`:

```csharp
using System.Text.Json;
using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

public class RunFileRecordTests
{
    [Fact]
    public void TestRoleAndOriginalNameRoundTripThroughJson()
    {
        RunFileRecord record = new()
        {
            Kind = "input",
            RelativePath = "0/IFRS9.csv",
            StoragePath = "u/r/input/0/IFRS9.csv",
            SizeBytes = 12,
            Role = "exposure",
            OriginalName = "IFRS9 FILE JUNE 2025.csv"
        };

        string json = JsonSerializer.Serialize(record);
        Assert.Contains("\"role\":\"exposure\"", json);
        Assert.Contains("\"original_name\":\"IFRS9 FILE JUNE 2025.csv\"", json);

        RunFileRecord back = JsonSerializer.Deserialize<RunFileRecord>(json)!;
        Assert.Equal("exposure", back.Role);
        Assert.Equal("IFRS9 FILE JUNE 2025.csv", back.OriginalName);
    }

    [Fact]
    public void TestRowsWrittenBeforeTheMigrationDeserialiseWithBothNull()
    {
        // a row from an older run: the columns did not exist when it was written
        string json = """
        {"kind":"input","relative_path":"0/writeoff.csv","storage_path":"u/r/input/0/writeoff.csv","size_bytes":9}
        """;

        RunFileRecord back = JsonSerializer.Deserialize<RunFileRecord>(json)!;

        Assert.Null(back.Role);
        Assert.Null(back.OriginalName);
        Assert.Equal("0/writeoff.csv", back.RelativePath);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~RunFileRecordTests`
Expected: FAIL — `RunFileRecord` has no `Role` or `OriginalName` member (compile error).

- [ ] **Step 3: Add the two properties**

In `src/HazardRecon.Web/Runs/RunFileRecord.cs`, after `SizeBytes`:

```csharp
    /// <summary>
    /// Which slot the user picked this file for: exposure | writeoff | debug |
    /// scenario. Null for output rows and for input rows written before this was
    /// recorded, where it is derived from the canonical file name instead.
    /// </summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    /// <summary>
    /// The name the file had when it was picked. SetFileReceiver renames exposure,
    /// write-off and scenario files to canonical names, so without this a card can
    /// only say "writeoff.csv", which identifies neither period nor source. Null
    /// for output rows and for input rows written before this was recorded.
    /// </summary>
    [JsonPropertyName("original_name")]
    public string? OriginalName { get; set; }
```

- [ ] **Step 4: Write the migration**

Create `supabase/migrations/20260805000000_run_file_roles.sql`:

```sql
-- Records which slot each uploaded input file was picked for, and the name it
-- was picked under, so a past run's inputs can be listed back onto the Files
-- step. See docs/superpowers/specs/2026-08-05-run-again-edit-inputs-design.md.
--
-- Re-runnable, like every migration here: this schema has been applied by hand
-- through the SQL editor as well as by the CLI (see docs/deployment.md), so
-- whether a database has already seen this file is not knowable from the
-- migration history alone.
--
-- Both columns are nullable on purpose. Rows written before this migration
-- cannot have them, and readers fall back to the canonical file name and a
-- role derived from it, so existing history keeps working.

alter table public.run_files add column if not exists role text;
alter table public.run_files add column if not exists original_name text;

-- input rows only; outputs have no slot
alter table public.run_files drop constraint if exists run_files_role_check;
alter table public.run_files add constraint run_files_role_check
  check (role is null or role in ('exposure', 'writeoff', 'debug', 'scenario'));

-- the inputs endpoint reads one run's input rows and nothing else
create index if not exists run_files_run_kind_idx on public.run_files (run_id, kind);
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~RunFileRecordTests`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add supabase/migrations/20260805000000_run_file_roles.sql src/HazardRecon.Web/Runs/RunFileRecord.cs tests/HazardRecon.Tests/Web/RunFileRecordTests.cs
git commit -m "feat: record each input file's role and original name"
```

---

### Task 2: `SetFileReceiver` reports what it wrote

`RunPersister` walks a directory and cannot know a file's role or original name. The receiver knows both; make it say so.

**Files:**
- Modify: `src/HazardRecon.Web/Uploads/SetFileReceiver.cs`
- Test: `tests/HazardRecon.Tests/Web/SetFileReceiverTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `record ReceivedFile(string RelativePath, string Role, string OriginalName)` and `ReceivedSet.Files` (`IReadOnlyList<ReceivedFile>`). `RelativePath` is relative to the destination root and uses `/`, e.g. `0/IFRS9.csv`.

- [ ] **Step 1: Write the failing test**

Append to `tests/HazardRecon.Tests/Web/SetFileReceiverTests.cs` (inside the existing class; reuse its existing helpers for building items and a temp directory — read the file first and match them):

```csharp
    [Fact]
    public async Task TestReportsEachFilesRoleOriginalNameAndRelativePath()
    {
        string dir = NewTempDir();
        var items = new List<SetFileItem>
        {
            Item(0, SetFileKind.Exposure, "IFRS9 FILE JUNE 2025.csv", "a,b\n1,2\n"),
            Item(0, SetFileKind.Writeoff, "2026_WRITEOFF.csv", "c,d\n3,4\n"),
            Item(0, SetFileKind.Debug, "debug.zip", "zip"),
        };

        SetReceiveOutcome outcome = await new SetFileReceiver().ReceiveAsync(dir, items);

        Assert.True(outcome.Ok);
        ReceivedSet set = Assert.Single(outcome.Sets);

        // canonical on disk, original name kept alongside it
        Assert.Equal(
            new[] { "0/IFRS9.csv", "0/writeoff.csv", "0/debug.zip" },
            set.Files.Select(f => f.RelativePath).ToArray());
        Assert.Equal(
            new[] { "exposure", "writeoff", "debug" },
            set.Files.Select(f => f.Role).ToArray());
        Assert.Equal("IFRS9 FILE JUNE 2025.csv",
            set.Files.Single(f => f.Role == "exposure").OriginalName);
        Assert.Equal("2026_WRITEOFF.csv",
            set.Files.Single(f => f.Role == "writeoff").OriginalName);
    }

    [Fact]
    public async Task TestRelativePathsCarryTheSetIndex()
    {
        string dir = NewTempDir();
        var items = new List<SetFileItem>
        {
            Item(0, SetFileKind.Exposure, "one.csv", "a\n1\n"),
            Item(1, SetFileKind.Exposure, "two.csv", "a\n1\n"),
        };

        SetReceiveOutcome outcome = await new SetFileReceiver().ReceiveAsync(dir, items);

        Assert.Equal("0/IFRS9.csv", outcome.Sets[0].Files.Single().RelativePath);
        Assert.Equal("1/IFRS9.csv", outcome.Sets[1].Files.Single().RelativePath);
    }
```

If `NewTempDir()` and `Item(...)` do not already exist in that file under those names, add them:

```csharp
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hr-receiver-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SetFileItem Item(int setIndex, SetFileKind kind, string name, string content)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
        return new SetFileItem(setIndex, kind, name, new MemoryStream(bytes), bytes.Length);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~SetFileReceiverTests`
Expected: FAIL — `ReceivedSet` has no `Files` member.

- [ ] **Step 3: Add the record and populate it**

In `src/HazardRecon.Web/Uploads/SetFileReceiver.cs`, add above `ReceivedSet`:

```csharp
/// <summary>
/// One file as written: where it landed relative to the destination root, the
/// slot it was picked for, and the name it was picked under. RunPersister walks
/// a directory and cannot know the last two, so the receiver reports them.
/// </summary>
public record ReceivedFile(string RelativePath, string Role, string OriginalName);
```

Extend `ReceivedSet` with a trailing parameter:

```csharp
public record ReceivedSet(
    string Root, string Label, string ExposureFileName, string? WriteOffFileName,
    int FileCount, long Bytes, IReadOnlyList<ReceivedFile> Files);
```

Inside the per-set loop, collect as each file is written. Replace the `foreach (SetFileItem item in setItems)` body's tail and the `sets.Add(...)` call:

```csharp
            List<ReceivedFile> written = new();

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

                written.Add(new ReceivedFile(
                    $"{setIndex}/{destName}",
                    item.Kind.ToString().ToLowerInvariant(),
                    item.OriginalFileName));
            }

            string label = Path.GetFileNameWithoutExtension(exposureName);
            sets.Add(new ReceivedSet(setRoot, label, exposureName, writeoffName, setItems.Count, total, written));
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~SetFileReceiverTests`
Expected: PASS. Fix any other call site the compiler flags (`Program.cs` constructs no `ReceivedSet`, so there should be none).

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Web/Uploads/SetFileReceiver.cs tests/HazardRecon.Tests/Web/SetFileReceiverTests.cs
git commit -m "feat: have the receiver report each file's role and original name"
```

---

### Task 3: `RunPersister` stores the role and original name

**Files:**
- Modify: `src/HazardRecon.Web/Runs/RunPersister.cs`, `src/HazardRecon.Web/Program.cs:254`
- Test: `tests/HazardRecon.Tests/Web/RunPersisterTests.cs`

**Interfaces:**
- Consumes: `ReceivedFile` from Task 2; `RunFileRecord.Role`/`OriginalName` from Task 1.
- Produces: `PersistDirectoryAsync(..., IReadOnlyDictionary<string, ReceivedFile>? describedBy = null, ...)` — keyed by the same `/`-separated relative path the walker computes.

- [ ] **Step 1: Write the failing test**

Append to `tests/HazardRecon.Tests/Web/RunPersisterTests.cs` (read it first and reuse its existing fixture helpers):

```csharp
    [Fact]
    public async Task TestRecordsRoleAndOriginalNameForDescribedFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hr-persist", Guid.NewGuid().ToString("N")[..8], "input");
        Directory.CreateDirectory(Path.Combine(dir, "0"));
        await File.WriteAllTextAsync(Path.Combine(dir, "0", "IFRS9.csv"), "a\n1\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "0", "debug.zip"), "zip");

        FakeFileStore files = new();
        FakeRunFileStore index = new();
        Guid user = Guid.NewGuid(), run = Guid.NewGuid();

        var described = new Dictionary<string, ReceivedFile>
        {
            ["0/IFRS9.csv"] = new("0/IFRS9.csv", "exposure", "IFRS9 FILE JUNE 2025.csv"),
            ["0/debug.zip"] = new("0/debug.zip", "debug", "debug.zip"),
        };

        await new RunPersister(files, index)
            .PersistDirectoryAsync(user, run, "input", dir, describedBy: described);

        RunFileRecord exposure = index.Files.Single(f => f.RelativePath == "0/IFRS9.csv");
        Assert.Equal("exposure", exposure.Role);
        Assert.Equal("IFRS9 FILE JUNE 2025.csv", exposure.OriginalName);
    }

    [Fact]
    public async Task TestLeavesRoleAndNameNullWhenNothingDescribesTheFile()
    {
        // outputs are persisted the same way and have no slot
        string dir = Path.Combine(Path.GetTempPath(), "hr-persist", Guid.NewGuid().ToString("N")[..8], "output");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "workbook.xlsx"), "x");

        FakeRunFileStore index = new();
        await new RunPersister(new FakeFileStore(), index)
            .PersistDirectoryAsync(Guid.NewGuid(), Guid.NewGuid(), "output", dir);

        RunFileRecord only = Assert.Single(index.Files);
        Assert.Null(only.Role);
        Assert.Null(only.OriginalName);
    }
```

Add `using HazardRecon.Web.Uploads;` at the top of the test file if absent.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~RunPersisterTests`
Expected: FAIL — no `describedBy` parameter.

- [ ] **Step 3: Add the parameter and use it**

In `RunPersister.cs`, add `using HazardRecon.Web.Uploads;` and extend the signature (new parameter last, so existing callers are unaffected):

```csharp
    public async Task<PersistOutcome> PersistDirectoryAsync(
        Guid userId,
        Guid runId,
        string kind,
        string directory,
        string? setKey = null,
        IReadOnlyDictionary<string, ReceivedFile>? describedBy = null,
        CancellationToken ct = default)
```

Inside the loop, after computing `relative`, look the description up and record it:

```csharp
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
```

- [ ] **Step 4: Pass the descriptions in at the call site**

In `Program.cs`, replace the input persistence call (`Program.cs:254`) with:

```csharp
            await persister.PersistDirectoryAsync(
                userId.Value, created.Id, "input", indir,
                describedBy: received.Sets
                    .SelectMany(s => s.Files)
                    .ToDictionary(f => f.RelativePath));
```

- [ ] **Step 5: Run the suite to verify it passes**

Run: `dotnet test tests/HazardRecon.Tests`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/HazardRecon.Web/Runs/RunPersister.cs src/HazardRecon.Web/Program.cs tests/HazardRecon.Tests/Web/RunPersisterTests.cs
git commit -m "feat: persist each input file's role and original name"
```

---

### Task 4: `RunInputs` turns index rows into per-set slots

A pure read model, unit-testable without HTTP — the same reason `ChatModel` exists.

**Files:**
- Create: `src/HazardRecon.Web/Runs/RunInputs.cs`
- Test: `tests/HazardRecon.Tests/Web/RunInputsTests.cs`

**Interfaces:**
- Consumes: `RunFileRecord` (Task 1). Requires `using System.Linq;` and `System.IO` — both are implicit in this project.
- Produces:
  - `record RunInputFile(string Role, string Name, long SizeBytes)`
  - `record RunInputSet(int Index, string Label, IReadOnlyList<RunInputFile> Files)`
  - `static IReadOnlyList<RunInputSet> RunInputs.Describe(IReadOnlyList<RunFileRecord> files, IReadOnlyList<string> setLabels)`
  - `static string RoleOf(RunFileRecord file)` — the recorded role, else derived from the canonical name.

- [ ] **Step 1: Write the failing test**

Create `tests/HazardRecon.Tests/Web/RunInputsTests.cs`:

```csharp
using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

public class RunInputsTests
{
    private static RunFileRecord Input(string relativePath, string? role = null,
        string? originalName = null, long size = 10) => new()
    {
        Kind = "input", RelativePath = relativePath, StoragePath = "s/" + relativePath,
        SizeBytes = size, Role = role, OriginalName = originalName
    };

    [Fact]
    public void TestGroupsInputsBySetIndexInOrder()
    {
        var files = new[]
        {
            Input("1/IFRS9.csv", "exposure", "JULY.csv"),
            Input("0/IFRS9.csv", "exposure", "JUNE.csv"),
        };

        var sets = RunInputs.Describe(files, new[] { "JUNE", "JULY" });

        Assert.Equal(new[] { 0, 1 }, sets.Select(s => s.Index).ToArray());
        Assert.Equal("JUNE", sets[0].Label);
        Assert.Equal("JULY", sets[1].Label);
    }

    [Fact]
    public void TestUsesTheOriginalNameAndRecordedRole()
    {
        var sets = RunInputs.Describe(
            new[] { Input("0/writeoff.csv", "writeoff", "2026_WRITEOFF.csv", 9000) },
            new[] { "JUNE" });

        RunInputFile file = Assert.Single(sets[0].Files);
        Assert.Equal("writeoff", file.Role);
        Assert.Equal("2026_WRITEOFF.csv", file.Name);
        Assert.Equal(9000, file.SizeBytes);
    }

    [Fact]
    public void TestFallsBackToTheCanonicalNameAndDerivedRoleForOlderRows()
    {
        // written before role/original_name existed
        var sets = RunInputs.Describe(
            new[]
            {
                Input("0/IFRS9.csv"), Input("0/writeoff.csv"),
                Input("0/scenario.json"), Input("0/debug.zip"),
            },
            new[] { "JUNE" });

        Assert.Equal(
            new[] { "debug", "exposure", "scenario", "writeoff" },
            sets[0].Files.Select(f => f.Role).OrderBy(r => r).ToArray());

        Assert.Equal("IFRS9.csv", sets[0].Files.Single(f => f.Role == "exposure").Name);
        Assert.Equal("debug.zip", sets[0].Files.Single(f => f.Role == "debug").Name);
    }

    [Fact]
    public void TestAnythingUnrecognisedInASetIsADebugFile()
    {
        // debug files keep their own names, so they cannot be matched by name -
        // they are what is left over
        var sets = RunInputs.Describe(
            new[] { Input("0/lgd_defaults.csv"), Input("0/pd_scored.csv") },
            new[] { "JUNE" });

        Assert.All(sets[0].Files, f => Assert.Equal("debug", f.Role));
    }

    [Fact]
    public void TestIgnoresOutputRows()
    {
        var files = new[]
        {
            Input("0/IFRS9.csv", "exposure", "JUNE.csv"),
            new RunFileRecord { Kind = "output", RelativePath = "workbook.xlsx", StoragePath = "s", SizeBytes = 1 },
        };

        var sets = RunInputs.Describe(files, new[] { "JUNE" });

        Assert.Single(Assert.Single(sets).Files);
    }

    [Fact]
    public void TestASetWithNoLabelStillDescribesItself()
    {
        var sets = RunInputs.Describe(new[] { Input("2/IFRS9.csv", "exposure", "X.csv") }, Array.Empty<string>());

        Assert.Equal(2, sets[0].Index);
        Assert.Equal("Set 3", sets[0].Label);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~RunInputsTests`
Expected: FAIL — `RunInputs` does not exist.

- [ ] **Step 3: Write the read model**

Create `src/HazardRecon.Web/Runs/RunInputs.cs`:

```csharp
namespace HazardRecon.Web.Runs;

/// <summary>One stored input file, as the Files step needs to show it.</summary>
public record RunInputFile(string Role, string Name, long SizeBytes);

/// <summary>One set's stored inputs, in the set order the run was uploaded in.</summary>
public record RunInputSet(int Index, string Label, IReadOnlyList<RunInputFile> Files);

/// <summary>
/// Turns a run's run_files index rows into per-set slots.
///
/// Input rows are keyed by set *index*, which is the leading segment of
/// relative_path ("0/IFRS9.csv") - PersistDirectoryAsync is called once for the
/// whole input directory, so set_key is null on these rows. Set labels come from
/// runs.set_labels, which is in the same order.
///
/// Role and original name are read where recorded and derived where not, so runs
/// created before those columns existed still list their inputs - with canonical
/// names, which is the best that can be done for them.
/// </summary>
public static class RunInputs
{
    public static string RoleOf(RunFileRecord file)
    {
        if (!string.IsNullOrEmpty(file.Role)) return file.Role;

        // debug files keep their own source-system names, so they cannot be
        // matched by name - they are whatever is not one of the three canonical
        // names the receiver assigns
        return Path.GetFileName(file.RelativePath).ToLowerInvariant() switch
        {
            "ifrs9.csv" => "exposure",
            "writeoff.csv" => "writeoff",
            "scenario.json" => "scenario",
            _ => "debug"
        };
    }

    private static int? SetIndexOf(RunFileRecord file)
    {
        string[] parts = file.RelativePath.Split('/');
        return parts.Length >= 2 && int.TryParse(parts[0], out int index) ? index : null;
    }

    public static IReadOnlyList<RunInputSet> Describe(
        IReadOnlyList<RunFileRecord> files, IReadOnlyList<string> setLabels)
    {
        return files
            .Where(f => f.Kind == "input")
            .Select(f => (File: f, Index: SetIndexOf(f)))
            .Where(x => x.Index != null)
            .GroupBy(x => x.Index!.Value)
            .OrderBy(g => g.Key)
            .Select(g => new RunInputSet(
                g.Key,
                g.Key < setLabels.Count ? setLabels[g.Key] : $"Set {g.Key + 1}",
                g.Select(x => new RunInputFile(
                        RoleOf(x.File),
                        string.IsNullOrEmpty(x.File.OriginalName)
                            ? Path.GetFileName(x.File.RelativePath)
                            : x.File.OriginalName,
                        x.File.SizeBytes))
                    .ToList()))
            .ToList();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~RunInputsTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Web/Runs/RunInputs.cs tests/HazardRecon.Tests/Web/RunInputsTests.cs
git commit -m "feat: describe a run's stored inputs as per-set slots"
```

---

### Task 5: `GET /api/runs/{rid}/inputs`

**Files:**
- Modify: `src/HazardRecon.Web/Program.cs` (add beside `GET /api/runs/{rid}`, `Program.cs:871`)
- Test: `tests/HazardRecon.Tests/Web/RunInputsEndpointTests.cs`

**Interfaces:**
- Consumes: `RunInputs.Describe` (Task 4); `IRunStore.GetAsync`, `IRunFileStore.ListAsync`.
- Produces: `GET /api/runs/{rid}/inputs` → `{ inputs_purged: bool, sets: [{ index, label, files: [{ role, name, size_bytes }] }] }`.

- [ ] **Step 1: Write the failing test**

Create `tests/HazardRecon.Tests/Web/RunInputsEndpointTests.cs`. Copy the fixture scaffolding (`AuthedFactory`, `AlwaysOnHandler`, the authed `HttpClient` helper) from `tests/HazardRecon.Tests/Web/DeleteRunEndpointTests.cs` — read that file and mirror its setup exactly, substituting the seeding below.

```csharp
    [Fact]
    public async Task TestListsEachSetsStoredInputs()
    {
        Guid run = SeedRun(setLabels: new[] { "JUNE 2026" });
        SeedInput(run, "0/IFRS9.csv", "exposure", "IFRS9 FILE JUNE 2025.csv", 12_800_000);
        SeedInput(run, "0/debug.zip", "debug", "debug.zip", 13_800_000);

        HttpResponseMessage res = await Client.GetAsync($"/api/runs/{run}/inputs");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());

        Assert.False(doc.RootElement.GetProperty("inputs_purged").GetBoolean());
        JsonElement set = doc.RootElement.GetProperty("sets")[0];
        Assert.Equal(0, set.GetProperty("index").GetInt32());
        Assert.Equal("JUNE 2026", set.GetProperty("label").GetString());

        JsonElement exposure = set.GetProperty("files").EnumerateArray()
            .Single(f => f.GetProperty("role").GetString() == "exposure");
        Assert.Equal("IFRS9 FILE JUNE 2025.csv", exposure.GetProperty("name").GetString());
        Assert.Equal(12_800_000, exposure.GetProperty("size_bytes").GetInt64());
    }

    [Fact]
    public async Task TestAPurgedRunSaysSoAndListsNoFiles()
    {
        Guid run = SeedRun(setLabels: new[] { "MAY 2026" }, inputsPurgedAt: DateTimeOffset.UtcNow);

        HttpResponseMessage res = await Client.GetAsync($"/api/runs/{run}/inputs");

        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("inputs_purged").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("sets").EnumerateArray());
    }

    [Fact]
    public async Task TestAnotherUsersRunIsNotFound()
    {
        Guid run = SeedRun(setLabels: new[] { "X" }, userId: Guid.NewGuid());

        HttpResponseMessage res = await Client.GetAsync($"/api/runs/{run}/inputs");

        // 404 not 403: a 403 would confirm the run exists
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task TestAnUnknownRunIsNotFound()
    {
        HttpResponseMessage res = await Client.GetAsync($"/api/runs/{Guid.NewGuid()}/inputs");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
```

Add these helpers to the test class. `TestUser` is the guid the fake auth handler puts in the `sub` claim — take its value from the fixture you copied.

```csharp
    private Guid SeedRun(IReadOnlyList<string> setLabels, Guid? userId = null,
        DateTimeOffset? inputsPurgedAt = null)
    {
        Guid id = Guid.NewGuid();
        Runs.Runs.Add(new RunRecord
        {
            Id = id,
            UserId = userId ?? TestUser,
            SetLabels = setLabels.ToList(),
            InputsPurgedAt = inputsPurgedAt
        });
        return id;
    }

    private void SeedInput(Guid runId, string relativePath, string role, string originalName, long size)
    {
        RunFiles.Files.Add(new RunFileRecord
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            UserId = TestUser,
            Kind = "input",
            RelativePath = relativePath,
            StoragePath = $"{TestUser}/{runId}/input/{relativePath}",
            SizeBytes = size,
            Role = role,
            OriginalName = originalName
        });
    }
```

`Runs` and `RunFiles` are the `FakeRunStore` and `FakeRunFileStore` the factory registered — expose them from the fixture as properties if the file you copied does not already.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~RunInputsEndpointTests`
Expected: FAIL — 404 for every case, because the route does not exist.

- [ ] **Step 3: Add the endpoint**

In `Program.cs`, directly after the `GET /api/runs/{rid}` handler:

```csharp
// GET /api/runs/{rid}/inputs - the files this run was given, so "Run again" can
// put them back on the Files step. Inputs are kept 30 days (InputPurger); after
// that the run says so and asks for them again.
app.MapGet("/api/runs/{rid}/inputs", async (string rid, HttpContext ctx) =>
{
    Guid? userId = SupabaseJwt.UserId(ctx.User);
    if (userId == null) return Results.Unauthorized();

    if (!Guid.TryParse(rid, out Guid runGuid)) return Results.NotFound(new { error = "Unknown run." });

    // 404 rather than 403 for someone else's run, as elsewhere: a 403 confirms it exists
    RunRecord? run = await runStore.GetAsync(runGuid, userId.Value, ctx.RequestAborted);
    if (run == null) return Results.NotFound(new { error = "Unknown run." });

    if (run.InputsPurgedAt != null)
        return Results.Ok(new { inputs_purged = true, sets = Array.Empty<object>() });

    IReadOnlyList<RunFileRecord> files = await runFileStore.ListAsync(runGuid, userId.Value, ctx.RequestAborted);

    return Results.Ok(new
    {
        inputs_purged = false,
        sets = RunInputs.Describe(files, run.SetLabels).Select(s => new
        {
            index = s.Index,
            label = s.Label,
            files = s.Files.Select(f => new { role = f.Role, name = f.Name, size_bytes = f.SizeBytes })
        })
    });
}).RequireAuthorization();
```

Match the surrounding handlers' service-resolution style: if they take stores as lambda parameters rather than closures, do the same for `runStore` and `runFileStore`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~RunInputsEndpointTests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Web/Program.cs tests/HazardRecon.Tests/Web/RunInputsEndpointTests.cs
git commit -m "feat: expose a run's stored input files"
```

---

### Task 6: Stream a stored object back to disk

Reused files must be materialised locally, because the engine reads inputs from disk (`job.Roots`). A debug file can be hundreds of megabytes, so this must not buffer — and `SupabaseRestClient.SendAsync` reads the whole body into a `string`.

**Files:**
- Modify: `src/HazardRecon.Web/Supabase/SupabaseRestClient.cs`, `src/HazardRecon.Web/Files/IFileStore.cs`, `src/HazardRecon.Web/Files/SupabaseFileStore.cs`, `tests/HazardRecon.Tests/Web/Fakes.cs`
- Test: `tests/HazardRecon.Tests/Web/SupabaseFileStoreTests.cs`

**Interfaces:**
- Produces:
  - `SupabaseRestClient.DownloadToFileAsync(string path, string destinationPath, CancellationToken ct = default)`
  - `IFileStore.DownloadToFileAsync(string storagePath, string destinationPath, CancellationToken ct = default)`
  - `FakeFileStore.Uploaded` gains readable content so the fake can serve downloads.

- [ ] **Step 1: Write the failing test**

Append to `tests/HazardRecon.Tests/Web/SupabaseFileStoreTests.cs` (reuse its existing stub `HttpMessageHandler` and options helper — read the file first):

```csharp
    [Fact]
    public async Task TestDownloadsAnObjectStraightToAFile()
    {
        StubHandler handler = new(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("account,balance\n1,2\n"))
        });
        SupabaseFileStore store = new(new SupabaseRestClient(Options(), handler), Options());

        string dest = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")[..8] + ".csv");
        await store.DownloadToFileAsync("u/r/input/0/IFRS9.csv", dest);

        Assert.Equal("account,balance\n1,2\n", await File.ReadAllTextAsync(dest));
        Assert.Contains("/storage/v1/object/runs/u/r/input/0/IFRS9.csv", handler.LastRequestUri!.ToString());
        File.Delete(dest);
    }

    [Fact]
    public async Task TestAMissingObjectThrowsRatherThanWritingAnEmptyFile()
    {
        StubHandler handler = new(req => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":\"not found\"}")
        });
        SupabaseFileStore store = new(new SupabaseRestClient(Options(), handler), Options());

        string dest = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")[..8] + ".csv");

        await Assert.ThrowsAsync<SupabaseException>(() => store.DownloadToFileAsync("u/r/input/0/gone.csv", dest));
        Assert.False(File.Exists(dest));
    }
```

If the existing stub handler does not record `LastRequestUri` or take a response factory, extend it to do so.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~SupabaseFileStoreTests`
Expected: FAIL — no `DownloadToFileAsync`.

- [ ] **Step 3: Add the streaming send**

In `SupabaseRestClient.cs`, after `SendAsync`:

```csharp
    /// <summary>
    /// Streams a GET straight into a file. Separate from SendAsync because that
    /// reads the whole body into a string, which a debug file of several hundred
    /// megabytes must not do. Nothing is written unless the response succeeds, so
    /// a failure cannot leave a truncated file behind.
    /// </summary>
    public async Task DownloadToFileAsync(string path, string destinationPath, CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, _options.BaseUrl + path);
        request.Headers.TryAddWithoutValidation("apikey", _options.ServiceRoleKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServiceRoleKey);

        using HttpResponseMessage response =
            await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            throw new SupabaseException((int)response.StatusCode,
                $"Supabase {(int)response.StatusCode} for GET {path}: {body}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        await using Stream source = await response.Content.ReadAsStreamAsync(ct);
        await using FileStream destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, ct);
    }
```

- [ ] **Step 4: Add it to the store interface and implementation**

In `IFileStore.cs`:

```csharp
    /// <summary>
    /// Writes a stored object to a local path, creating parent directories.
    /// Streamed, not buffered: run inputs include debug files of hundreds of
    /// megabytes. Throws if the object is not there, rather than leaving an empty
    /// file that would read as a valid but empty input.
    /// </summary>
    Task DownloadToFileAsync(string storagePath, string destinationPath, CancellationToken ct = default);
```

In `SupabaseFileStore.cs`:

```csharp
    public Task DownloadToFileAsync(string storagePath, string destinationPath, CancellationToken ct = default) =>
        _rest.DownloadToFileAsync($"/storage/v1/object/{_bucket}/{storagePath}", destinationPath, ct);
```

- [ ] **Step 5: Teach the fake to serve what it stored**

In `tests/HazardRecon.Tests/Web/Fakes.cs`, `FakeFileStore` records uploads; make the bytes retrievable and add the download. Keep whatever the existing `Uploaded` collection is and add alongside it:

```csharp
    public Dictionary<string, byte[]> Contents { get; } = new();

    public Task DownloadToFileAsync(string storagePath, string destinationPath, CancellationToken ct = default)
    {
        if (!Contents.TryGetValue(storagePath, out byte[]? bytes))
            throw new FileNotFoundException($"nothing stored at {storagePath}");

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllBytes(destinationPath, bytes);
        return Task.CompletedTask;
    }
```

In its `UploadAsync`, also fill `Contents`:

```csharp
        using MemoryStream buffer = new();
        content.CopyTo(buffer);
        Contents[storagePath] = buffer.ToArray();
```

- [ ] **Step 6: Run the suite to verify it passes**

Run: `dotnet test tests/HazardRecon.Tests`
Expected: PASS. Any other `IFileStore` implementation in the tests must gain the method too — the compiler will name it.

- [ ] **Step 7: Commit**

```bash
git add src/HazardRecon.Web/Supabase/SupabaseRestClient.cs src/HazardRecon.Web/Files/IFileStore.cs src/HazardRecon.Web/Files/SupabaseFileStore.cs tests/HazardRecon.Tests/Web/Fakes.cs tests/HazardRecon.Tests/Web/SupabaseFileStoreTests.cs
git commit -m "feat: stream a stored object back to a local file"
```

---

### Task 7: `InputReuse` rebuilds requested roles as upload items

Reused files become ordinary `SetFileItem`s, so `SetFileReceiver` needs no change: canonical naming, the exposure requirement, per-set size limits and set labelling all keep working.

**Files:**
- Create: `src/HazardRecon.Web/Uploads/InputReuse.cs`
- Test: `tests/HazardRecon.Tests/Web/InputReuseTests.cs`

**Interfaces:**
- Consumes: `IFileStore.DownloadToFileAsync` (Task 6); `RunInputs.RoleOf` (Task 4); `SetFileItem`, `SetFileKind`.
- Produces:
  - `record ReuseRequest(int SetIndex, IReadOnlyList<string> Roles)`
  - `record ReuseOutcome(bool Ok, string? Error, IReadOnlyList<SetFileItem> Items, IReadOnlyList<IDisposable> Open)`
  - `static Task<ReuseOutcome> InputReuse.MaterialiseAsync(IReadOnlyList<ReuseRequest> requests, IReadOnlyList<RunFileRecord> previousFiles, IFileStore storage, string tempRoot, CancellationToken ct = default)`

- [ ] **Step 1: Write the failing test**

Create `tests/HazardRecon.Tests/Web/InputReuseTests.cs`:

```csharp
using System.Text;
using HazardRecon.Web.Runs;
using HazardRecon.Web.Uploads;
using Xunit;

namespace HazardRecon.Tests.Web;

public class InputReuseTests
{
    private static string NewTempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hr-reuse", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (FakeFileStore Store, RunFileRecord Record) Stored(
        string relativePath, string role, string originalName, string content)
    {
        FakeFileStore store = new();
        string storagePath = "u/r/input/" + relativePath;
        store.Contents[storagePath] = Encoding.UTF8.GetBytes(content);

        return (store, new RunFileRecord
        {
            Kind = "input", RelativePath = relativePath, StoragePath = storagePath,
            SizeBytes = content.Length, Role = role, OriginalName = originalName
        });
    }

    [Fact]
    public async Task TestRebuildsARequestedRoleAsAnUploadItem()
    {
        var (store, record) = Stored("0/IFRS9.csv", "exposure", "IFRS9 JUNE.csv", "a,b\n1,2\n");

        ReuseOutcome outcome = await InputReuse.MaterialiseAsync(
            new[] { new ReuseRequest(0, new[] { "exposure" }) },
            new[] { record }, store, NewTempRoot());

        Assert.True(outcome.Ok);
        SetFileItem item = Assert.Single(outcome.Items);
        Assert.Equal(0, item.SetIndex);
        Assert.Equal(SetFileKind.Exposure, item.Kind);
        // the original name travels, so the receiver labels the set as before
        Assert.Equal("IFRS9 JUNE.csv", item.OriginalFileName);
        Assert.Equal(8, item.Length);

        using StreamReader reader = new(item.Content);
        Assert.Equal("a,b\n1,2\n", await reader.ReadToEndAsync());

        foreach (IDisposable d in outcome.Open) d.Dispose();
    }

    [Fact]
    public async Task TestRebuildsEveryDebugFileOfASet()
    {
        FakeFileStore store = new();
        store.Contents["u/r/input/0/lgd_defaults.csv"] = Encoding.UTF8.GetBytes("x");
        store.Contents["u/r/input/0/pd_scored.csv"] = Encoding.UTF8.GetBytes("y");

        var records = new[]
        {
            new RunFileRecord { Kind = "input", RelativePath = "0/lgd_defaults.csv",
                StoragePath = "u/r/input/0/lgd_defaults.csv", SizeBytes = 1, Role = "debug", OriginalName = "lgd_defaults.csv" },
            new RunFileRecord { Kind = "input", RelativePath = "0/pd_scored.csv",
                StoragePath = "u/r/input/0/pd_scored.csv", SizeBytes = 1, Role = "debug", OriginalName = "pd_scored.csv" },
        };

        ReuseOutcome outcome = await InputReuse.MaterialiseAsync(
            new[] { new ReuseRequest(0, new[] { "debug" }) }, records, store, NewTempRoot());

        Assert.True(outcome.Ok);
        Assert.Equal(2, outcome.Items.Count);
        Assert.All(outcome.Items, i => Assert.Equal(SetFileKind.Debug, i.Kind));

        foreach (IDisposable d in outcome.Open) d.Dispose();
    }

    [Fact]
    public async Task TestARoleThePreviousRunDoesNotHaveIsRefusedByName()
    {
        var (store, record) = Stored("0/IFRS9.csv", "exposure", "IFRS9 JUNE.csv", "a\n1\n");

        ReuseOutcome outcome = await InputReuse.MaterialiseAsync(
            new[] { new ReuseRequest(0, new[] { "exposure", "writeoff" }) },
            new[] { record }, store, NewTempRoot());

        Assert.False(outcome.Ok);
        Assert.Contains("writeoff", outcome.Error);
        Assert.Empty(outcome.Items);
    }

    [Fact]
    public async Task TestAnObjectMissingFromStorageIsRefusedByName()
    {
        var (store, record) = Stored("0/IFRS9.csv", "exposure", "IFRS9 JUNE.csv", "a\n1\n");
        store.Contents.Clear();   // indexed, but gone from the bucket

        ReuseOutcome outcome = await InputReuse.MaterialiseAsync(
            new[] { new ReuseRequest(0, new[] { "exposure" }) },
            new[] { record }, store, NewTempRoot());

        Assert.False(outcome.Ok);
        Assert.Contains("exposure", outcome.Error);
    }

    [Fact]
    public async Task TestOnlyTheRequestedRolesAreRebuilt()
    {
        FakeFileStore store = new();
        store.Contents["u/r/input/0/IFRS9.csv"] = Encoding.UTF8.GetBytes("a");
        store.Contents["u/r/input/0/writeoff.csv"] = Encoding.UTF8.GetBytes("b");

        var records = new[]
        {
            new RunFileRecord { Kind = "input", RelativePath = "0/IFRS9.csv",
                StoragePath = "u/r/input/0/IFRS9.csv", SizeBytes = 1, Role = "exposure", OriginalName = "e.csv" },
            new RunFileRecord { Kind = "input", RelativePath = "0/writeoff.csv",
                StoragePath = "u/r/input/0/writeoff.csv", SizeBytes = 1, Role = "writeoff", OriginalName = "w.csv" },
        };

        // the write-off file is being replaced, so only the exposure file is reused
        ReuseOutcome outcome = await InputReuse.MaterialiseAsync(
            new[] { new ReuseRequest(0, new[] { "exposure" }) }, records, store, NewTempRoot());

        Assert.True(outcome.Ok);
        Assert.Equal(SetFileKind.Exposure, Assert.Single(outcome.Items).Kind);

        foreach (IDisposable d in outcome.Open) d.Dispose();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~InputReuseTests`
Expected: FAIL — `InputReuse` does not exist.

- [ ] **Step 3: Write it**

Create `src/HazardRecon.Web/Uploads/InputReuse.cs`:

```csharp
using HazardRecon.Web.Files;
using HazardRecon.Web.Runs;

namespace HazardRecon.Web.Uploads;

/// <summary>The roles of one set that should come from the previous run rather than the upload.</summary>
public record ReuseRequest(int SetIndex, IReadOnlyList<string> Roles);

/// <summary>
/// Reused files as upload items, plus the handles holding them open. The caller
/// disposes Open after the receiver has copied them.
/// </summary>
public record ReuseOutcome(
    bool Ok, string? Error, IReadOnlyList<SetFileItem> Items, IReadOnlyList<IDisposable> Open)
{
    public static ReuseOutcome Fail(string error) =>
        new(false, error, Array.Empty<SetFileItem>(), Array.Empty<IDisposable>());
}

/// <summary>
/// Rebuilds a previous run's stored inputs as ordinary SetFileItems, so a re-run
/// that replaces one file does not have to re-upload the rest.
///
/// Deliberately produces upload items rather than writing into the new run's
/// input directory itself: handing them to SetFileReceiver alongside the real
/// uploads means canonical naming, the exposure requirement, the per-set size
/// limit and the set label all keep working, with no second code path that could
/// drift from the first.
/// </summary>
public static class InputReuse
{
    private static SetFileKind? KindOf(string role) => role switch
    {
        "exposure" => SetFileKind.Exposure,
        "writeoff" => SetFileKind.Writeoff,
        "debug" => SetFileKind.Debug,
        "scenario" => SetFileKind.Scenario,
        _ => null
    };

    public static async Task<ReuseOutcome> MaterialiseAsync(
        IReadOnlyList<ReuseRequest> requests,
        IReadOnlyList<RunFileRecord> previousFiles,
        IFileStore storage,
        string tempRoot,
        CancellationToken ct = default)
    {
        List<SetFileItem> items = new();
        List<IDisposable> open = new();

        // set index -> role -> the rows for it, from the previous run's index
        var bySet = previousFiles
            .Where(f => f.Kind == "input")
            .Select(f => (File: f, Parts: f.RelativePath.Split('/')))
            .Where(x => x.Parts.Length >= 2 && int.TryParse(x.Parts[0], out _))
            .GroupBy(x => int.Parse(x.Parts[0]))
            .ToDictionary(g => g.Key, g => g.Select(x => x.File).ToList());

        foreach (ReuseRequest request in requests)
        {
            bySet.TryGetValue(request.SetIndex, out List<RunFileRecord>? setFiles);
            setFiles ??= new List<RunFileRecord>();

            foreach (string role in request.Roles)
            {
                SetFileKind? kind = KindOf(role);
                if (kind == null)
                {
                    Dispose(open);
                    return ReuseOutcome.Fail($"Unknown file role '{role}'.");
                }

                List<RunFileRecord> matching = setFiles.Where(f => RunInputs.RoleOf(f) == role).ToList();
                if (matching.Count == 0)
                {
                    Dispose(open);
                    return ReuseOutcome.Fail(
                        $"Set {request.SetIndex + 1} has no stored {role} file to reuse - please choose it again.");
                }

                foreach (RunFileRecord file in matching)
                {
                    string destination = Path.Combine(
                        tempRoot, request.SetIndex.ToString(), Path.GetFileName(file.RelativePath));

                    try
                    {
                        await storage.DownloadToFileAsync(file.StoragePath, destination, ct);
                    }
                    catch (Exception)
                    {
                        // indexed but not in the bucket: name the slot, because the
                        // user's only way forward is to pick that file again
                        Dispose(open);
                        return ReuseOutcome.Fail(
                            $"Set {request.SetIndex + 1}'s stored {role} file could not be read - please choose it again.");
                    }

                    FileStream content = File.OpenRead(destination);
                    open.Add(content);

                    items.Add(new SetFileItem(
                        request.SetIndex,
                        kind.Value,
                        string.IsNullOrEmpty(file.OriginalName)
                            ? Path.GetFileName(file.RelativePath)
                            : file.OriginalName,
                        content,
                        content.Length));
                }
            }
        }

        return new ReuseOutcome(true, null, items, open);
    }

    private static void Dispose(IReadOnlyList<IDisposable> open)
    {
        foreach (IDisposable d in open) d.Dispose();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~InputReuseTests`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Web/Uploads/InputReuse.cs tests/HazardRecon.Tests/Web/InputReuseTests.cs
git commit -m "feat: rebuild a previous run's inputs as upload items"
```

---

### Task 8: `/api/discover` accepts `based_on_run` and `reuse`

**Files:**
- Modify: `src/HazardRecon.Web/Program.cs` (the `/api/discover` handler, from `Program.cs:168`)
- Test: `tests/HazardRecon.Tests/Web/UploadEndpointTests.cs`

**Interfaces:**
- Consumes: `InputReuse.MaterialiseAsync` (Task 7); `IRunStore.GetAsync`; `IRunFileStore.ListAsync`.
- Produces: two optional form fields on `/api/discover` — `based_on_run` (a run id) and `reuse` (JSON `[{"set":0,"roles":["exposure","debug"]}]`).

- [ ] **Step 1: Write the failing test**

Append to `tests/HazardRecon.Tests/Web/UploadEndpointTests.cs`, following its existing multipart helpers:

```csharp
    [Fact]
    public async Task TestReusesTheStoredFilesTheUploadDoesNotReplace()
    {
        Guid previous = SeedRunWithStoredInputs();   // exposure + writeoff + debug

        MultipartFormDataContent form = new();
        AddFile(form, "set0.Writeoff", "NEW_WRITEOFF.csv", "c,d\n3,4\n");
        form.Add(new StringContent(previous.ToString()), "based_on_run");
        form.Add(new StringContent("""[{"set":0,"roles":["exposure","debug"]}]"""), "reuse");

        HttpResponseMessage res = await Client.PostAsync("/api/discover", form);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using JsonDocument doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());

        // one set discovered, from one uploaded file plus two reused ones
        JsonElement set = Assert.Single(doc.RootElement.GetProperty("inventory").GetProperty("sets").EnumerateArray());
        Assert.Equal("writeoff.csv", set.GetProperty("writeoff").GetString());
        Assert.Equal("IFRS9.csv", set.GetProperty("ifrs9").GetString());
    }

    [Fact]
    public async Task TestReusingEverythingNeedsNoUploadedFileAtAll()
    {
        Guid previous = SeedRunWithStoredInputs();

        MultipartFormDataContent form = new();
        form.Add(new StringContent(previous.ToString()), "based_on_run");
        form.Add(new StringContent("""[{"set":0,"roles":["exposure","writeoff","debug"]}]"""), "reuse");

        HttpResponseMessage res = await Client.PostAsync("/api/discover", form);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task TestReusingAPurgedRunNamesWhatMustBePickedAgain()
    {
        Guid previous = SeedRunWithStoredInputs(inputsPurgedAt: DateTimeOffset.UtcNow);

        MultipartFormDataContent form = new();
        form.Add(new StringContent(previous.ToString()), "based_on_run");
        form.Add(new StringContent("""[{"set":0,"roles":["exposure"]}]"""), "reuse");

        HttpResponseMessage res = await Client.PostAsync("/api/discover", form);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("expired", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestReusingAnotherUsersRunIsNotFound()
    {
        Guid previous = SeedRunWithStoredInputs(userId: Guid.NewGuid());

        MultipartFormDataContent form = new();
        form.Add(new StringContent(previous.ToString()), "based_on_run");
        form.Add(new StringContent("""[{"set":0,"roles":["exposure"]}]"""), "reuse");

        HttpResponseMessage res = await Client.PostAsync("/api/discover", form);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
```

Add this helper. The debug file must be a **real** zip containing `lgd_defaults.csv`, because `InputDiscoverer.BuildSet` extracts it and a set with no `lgd_defaults.csv` is not discovered at all.

```csharp
    private static byte[] DebugZip()
    {
        using MemoryStream buffer = new();
        using (System.IO.Compression.ZipArchive zip =
               new(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            System.IO.Compression.ZipArchiveEntry entry = zip.CreateEntry("lgd_defaults.csv");
            using StreamWriter writer = new(entry.Open());
            writer.Write("AccountNumber,EventType,CohortDate,Bucket,Rating,Amount\n"
                       + "A1,Lifetime,2026-01-31,0,5,100\n");
        }
        return buffer.ToArray();
    }

    private Guid SeedRunWithStoredInputs(Guid? userId = null, DateTimeOffset? inputsPurgedAt = null)
    {
        Guid owner = userId ?? TestUser;
        Guid run = Guid.NewGuid();

        Runs.Runs.Add(new RunRecord
        {
            Id = run, UserId = owner,
            SetLabels = new List<string> { "JUNE 2026" },
            InputsPurgedAt = inputsPurgedAt
        });

        void Add(string relativePath, string role, string originalName, byte[] bytes)
        {
            string storagePath = $"{owner}/{run}/input/{relativePath}";
            RunFiles.Files.Add(new RunFileRecord
            {
                Id = Guid.NewGuid(), RunId = run, UserId = owner, Kind = "input",
                RelativePath = relativePath, StoragePath = storagePath,
                SizeBytes = bytes.Length, Role = role, OriginalName = originalName
            });
            Files.Contents[storagePath] = bytes;
        }

        Add("0/IFRS9.csv", "exposure", "IFRS9 FILE JUNE 2025.csv",
            Encoding.UTF8.GetBytes("LoanAccountNumber,AmountOutstanding\nA1,100\n"));
        Add("0/writeoff.csv", "writeoff", "2026_WRITEOFF.csv",
            Encoding.UTF8.GetBytes("LoanAccountNumber,CustomerId,Amount,ReportDate\nA1,C1,50,2026-01-31\n"));
        Add("0/debug.zip", "debug", "debug.zip", DebugZip());

        return run;
    }
```

`Runs`, `RunFiles` and `Files` are the `FakeRunStore`, `FakeRunFileStore` and `FakeFileStore` the factory registered; expose them from the fixture if they are not already. `AddFile` is the existing helper for adding a named file part — match its signature.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~UploadEndpointTests`
Expected: FAIL — the reuse fields are ignored, so the first test reports no exposure file.

- [ ] **Step 3: Read the two fields and materialise before receiving**

In `Program.cs`, in the `/api/discover` handler after `items` is built and before `SetFileReceiver` is called, insert:

```csharp
    // Re-running an earlier run: the roles the client did not re-upload are
    // rebuilt from that run's stored objects and handed to the receiver as if
    // they had been uploaded, so nothing below needs to know the difference.
    List<IDisposable> reusedHandles = new();
    string? basedOn = form["based_on_run"].FirstOrDefault();

    if (!string.IsNullOrEmpty(basedOn))
    {
        if (!Guid.TryParse(basedOn, out Guid previousRun))
            return Results.NotFound(new { error = "Unknown run." });

        RunRecord? previous = await runStore.GetAsync(previousRun, userId.Value, ctx.RequestAborted);
        if (previous == null) return Results.NotFound(new { error = "Unknown run." });

        if (previous.InputsPurgedAt != null)
            return Results.BadRequest(new
            {
                error = "The files from that run have expired, so they cannot be reused - please choose them again."
            });

        List<ReuseRequest> requests = new();
        string? reuseJson = form["reuse"].FirstOrDefault();
        if (!string.IsNullOrEmpty(reuseJson))
        {
            try
            {
                using JsonDocument reuseDoc = JsonDocument.Parse(reuseJson);
                foreach (JsonElement entry in reuseDoc.RootElement.EnumerateArray())
                {
                    requests.Add(new ReuseRequest(
                        entry.GetProperty("set").GetInt32(),
                        entry.GetProperty("roles").EnumerateArray().Select(r => r.GetString() ?? "").ToList()));
                }
            }
            catch (Exception)
            {
                return Results.BadRequest(new { error = "Could not read which files to reuse." });
            }
        }

        if (requests.Count > 0)
        {
            IReadOnlyList<RunFileRecord> previousFiles =
                await runFileStore.ListAsync(previousRun, userId.Value, ctx.RequestAborted);

            // outside indir, so it is never persisted as part of this run
            string reuseTemp = Path.Combine(runsDir, rid, "_reuse");

            ReuseOutcome reuse = await InputReuse.MaterialiseAsync(
                requests, previousFiles, fileStore, reuseTemp, ctx.RequestAborted);

            if (!reuse.Ok)
            {
                Directory.Delete(Path.Combine(runsDir, rid), recursive: true);
                await runStore.UpdateStatusAsync(created.Id, "error", reuse.Error);
                return Results.BadRequest(new { error = reuse.Error });
            }

            items.AddRange(reuse.Items);
            reusedHandles.AddRange(reuse.Open);
        }
    }
```

This must sit after `rid`/`created` and the directory creation exist, since it writes under the run root. Move the `items` construction above it if the current order requires.

Then, after `ReceiveAsync` returns, release the handles and drop the temp copies:

```csharp
    foreach (IDisposable handle in reusedHandles) handle.Dispose();

    string reuseDir = Path.Combine(runsDir, rid, "_reuse");
    if (Directory.Exists(reuseDir)) Directory.Delete(reuseDir, recursive: true);
```

Place this immediately after the `if (!received.Ok)` block so it runs on both paths — or wrap the receive in `try`/`finally` if that reads better in context.

Add `using HazardRecon.Web.Uploads;` if not already imported.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/HazardRecon.Tests --filter FullyQualifiedName~UploadEndpointTests`
Expected: PASS, including the pre-existing tests.

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test tests/HazardRecon.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/HazardRecon.Web/Program.cs tests/HazardRecon.Tests/Web/UploadEndpointTests.cs
git commit -m "feat: let discovery reuse a previous run's input files"
```

---

### Task 9: Slots can hold a stored file as well as a picked one

**Files:**
- Modify: `src/HazardRecon.Web/wwwroot/app.js` (`setBytes` ~`:547`, `slotSub` ~`:564`, `renderSets` ~`:571`, `updateReady` ~`:649`, `discover` ~`:728`)
- Test: `tests/client/app.harness.mjs`

**Interfaces:**
- Produces:
  - A stored-file descriptor: `{ name, size, fromRun }` — `fromRun` is the run id its bytes live under.
  - `const isStored = (f) => !!f && !!f.fromRun;`
  - `const reusePayload = () => [{ set: <index>, roles: [...] }]` — sets and roles still held as descriptors.
  - `BASED_ON` — module-level run id the descriptors came from, or `null`.

- [ ] **Step 1: Write the failing test**

Add to `tests/client/app.harness.mjs`, before the runner list. Reuse the `pickInto`/`setBlock`/`setHead`/`mkFile` helpers already there:

```js
/* ---------------- DT: reused files behave like picked ones ---------------- */
async function scenarioDT() {
  console.log("DT) a slot filled from a past run renders and uploads like a picked file");
  const posted = [];
  const fields = [];
  const h = bootAuth({ access_token: "tok-abc" }, (url, opts) => {
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (url === "/api/discover") {
      (opts.body._parts || []).forEach(p => {
        if (p.filename === undefined) fields.push({ field: p.field, value: p.value });
        else posted.push(p);
      });
      return Promise.resolve(jsonRes(200,
        { run_id: "RID-NEW", inventory: { root: "r", sets: [] }, problems: [], mapping: [] }));
    }
    return Promise.resolve(jsonRes(200, []));
  });
  await tick(); await tick(); await tick();

  // as "Run again" builds it: every slot from the stored run
  h.ctx.adoptStoredInputs("RID-OLD", [{
    index: 0, label: "JUNE 2026",
    files: [
      { role: "exposure", name: "IFRS9 FILE JUNE 2025.csv", size_bytes: 12 * 1024 * 1024 },
      { role: "debug", name: "debug.zip", size_bytes: 13 * 1024 * 1024 },
    ],
  }]);

  check("the stored names are shown", /IFRS9 FILE JUNE 2025\.csv · 12\.0 MB/.test(slotSubText(h, 0, "exposure")),
    `sub='${slotSubText(h, 0, "exposure")}'`);
  check("the set counts them as chosen", /2 of 4 files chosen/.test(setHead(h, 0)), `head='${setHead(h, 0)}'`);
  check("check columns is enabled", h.$get("#btn-check").disabled === false);

  // replace only the exposure file
  pickInto(h, 0, "exposure", [mkFile("NEW_IFRS9.csv", 2048)]);
  await h.ctx.discover();

  check("only the replaced file is uploaded",
    posted.length === 1 && posted[0].field === "set0.Exposure",
    JSON.stringify(posted.map(p => p.field)));
  check("the previous run is named", fields.some(f => f.field === "based_on_run" && f.value === "RID-OLD"),
    JSON.stringify(fields));

  const reuse = fields.find(f => f.field === "reuse");
  check("only the untouched role is reused", reuse !== undefined &&
    JSON.parse(reuse.value).length === 1 &&
    JSON.parse(reuse.value)[0].roles.join() === "debug",
    reuse ? reuse.value : "no reuse field");
}

/* ---------------- DU: a fully replaced set stops reusing ---------------- */
async function scenarioDU() {
  console.log("DU) replacing every slot sends no reuse request at all");
  const fields = [];
  const h = bootAuth({ access_token: "tok-abc" }, (url, opts) => {
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (url === "/api/discover") {
      (opts.body._parts || []).forEach(p => { if (p.filename === undefined) fields.push(p.field); });
      return Promise.resolve(jsonRes(200,
        { run_id: "RID-NEW", inventory: { root: "r", sets: [] }, problems: [], mapping: [] }));
    }
    return Promise.resolve(jsonRes(200, []));
  });
  await tick(); await tick(); await tick();

  h.ctx.adoptStoredInputs("RID-OLD", [{
    index: 0, label: "JUNE",
    files: [{ role: "exposure", name: "e.csv", size_bytes: 10 }, { role: "debug", name: "d.zip", size_bytes: 10 }],
  }]);

  pickInto(h, 0, "exposure", [mkFile("new-e.csv", 10)]);
  pickInto(h, 0, "debug", [mkFile("new-d.zip", 10)]);
  await h.ctx.discover();

  check("no reuse field is sent", !fields.includes("reuse"), JSON.stringify(fields));
  check("no previous run is named", !fields.includes("based_on_run"), JSON.stringify(fields));
}

/* ---------------- DUU: a set added on a re-run is uploaded in full ---------------- */
async function scenarioDUU() {
  console.log("DUU) adding a set to a re-run uploads it whole and reuses only the old one");
  const posted = [];
  let reuseValue = null;
  const h = bootAuth({ access_token: "tok-abc" }, (url, opts) => {
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (url === "/api/discover") {
      (opts.body._parts || []).forEach(p => {
        if (p.filename === undefined) { if (p.field === "reuse") reuseValue = p.value; }
        else posted.push(p.field);
      });
      return Promise.resolve(jsonRes(200,
        { run_id: "RID-NEW", inventory: { root: "r", sets: [] }, problems: [], mapping: [] }));
    }
    return Promise.resolve(jsonRes(200, []));
  });
  await tick(); await tick(); await tick();

  h.ctx.adoptStoredInputs("RID-OLD", [{
    index: 0, label: "JUNE",
    files: [{ role: "exposure", name: "e.csv", size_bytes: 10 }, { role: "debug", name: "d.zip", size_bytes: 10 }],
  }]);

  // a second period, picked fresh
  h.$get("#btn-add-set")._fire("click");
  pickInto(h, 1, "exposure", [mkFile("july-e.csv", 10)]);
  pickInto(h, 1, "debug", [mkFile("july-d.zip", 10)]);
  await h.ctx.discover();

  check("only the new set's files are uploaded",
    posted.join() === "set1.Exposure,set1.Debug", JSON.stringify(posted));
  check("only the old set is reused", reuseValue !== null &&
    JSON.parse(reuseValue).length === 1 && JSON.parse(reuseValue)[0].set === 0,
    reuseValue || "no reuse field");
}
```

Register all three (`scenarioDT`, `scenarioDU`, `scenarioDUU`) in the runner list at the bottom, after `scenarioDS`.

The harness's `FormData` stub records `{field, value, filename}`; a `append(field, value)` call with two arguments leaves `filename` undefined, which is how the checks tell fields from files.

- [ ] **Step 2: Run test to verify it fails**

Run: `node tests/client/app.harness.mjs`
Expected: FAIL — `h.ctx.adoptStoredInputs is not a function`.

- [ ] **Step 3: Add descriptors and the adopt helper**

In `app.js`, after `setStarted` (~`:555`):

```js
/* A slot may hold files the browser never opened: the ones a past run was given,
   which are still in object storage. They render exactly like a picked file - a
   name and a size is all a slot shows - and are sent as a reuse request instead
   of bytes. */
const isStored = (f) => !!f && !!f.fromRun;
const storedFile = (runId, f) => ({ name: f.name, size: f.size_bytes, fromRun: runId });

/* The run whose stored files fill the slots, or null once nothing is reused. */
let BASED_ON = null;

/* Note there is deliberately no standalone reusePayload(): the set numbers in it
   have to be the ones discover() puts in its field names, and discover() skips
   sets with nothing in them. Two separate walks would disagree the moment an
   empty set sat in front of a full one - the upload would say set0 while the
   reuse request said set 1. One walk builds both. */

/* Fills the picker from a past run's stored inputs. */
function adoptStoredInputs(runId, sets) {
  BASED_ON = runId;
  SETS = sets.map(s => {
    const set = emptySet();
    (s.files || []).forEach(f => {
      const kind = FILE_KINDS.find(k => k.key === f.role);
      if (kind) set.files[kind.key].push(storedFile(runId, f));
    });
    return set;
  });
  if (!SETS.length) SETS = [emptySet()];
  renderSets();
}
```

`FILE_KINDS[].key` is `exposure`/`writeoff`/`debug`/`scenario` — the same strings the server uses for a role — while `.field` is the capitalised form used only in the upload field name (`set0.Exposure`). Use `key` for roles and `field` for field names; do not mix them.

In `discover()`, send only real files and add the reuse fields. Replace its body's upload loop and add before the `api(...)` call:

```js
  const reuse = [];

  SETS.forEach(set => {
    const kinds = kindsPicked(set);
    if (!kinds.length) return;

    // the files this upload carries
    kinds.forEach(kind =>
      set.files[kind.key]
        .filter(f => !isStored(f))
        .forEach(f => fd.append("set" + sets + "." + kind.field, f, f.name)));

    // and the roles the server should rebuild from the previous run, numbered
    // with the same counter so the two can never disagree
    const reused = kinds.filter(kind => set.files[kind.key].some(isStored)).map(kind => kind.key);
    if (reused.length) reuse.push({ set: sets, roles: reused });

    sets += 1;
  });

  if (reuse.length && BASED_ON) {
    fd.append("based_on_run", BASED_ON);
    fd.append("reuse", JSON.stringify(reuse));
  }

  if (sets === 0) return Promise.reject(new Error("Please choose the files for at least one set."));
```

`kindsPicked` counts a slot holding descriptors, so a set contributing only reused roles still increments `sets` and still gets a number — which is exactly what makes "replace nothing, re-run" work.

In `setBytes`, count both — the server's per-set limit applies to the whole set, reused bytes included, so counting only uploads here would let the browser pass a set the server then rejects:

```js
const setBytes = (set) =>
  FILE_KINDS.reduce((n, k) => n + set.files[k.key].reduce((m, f) => m + (f.size || 0), 0), 0);
```

(That is already the existing implementation — a descriptor's `.size` makes it work unchanged. Confirm rather than edit.)

- [ ] **Step 4: Run the harness to verify it passes**

Run: `node tests/client/app.harness.mjs`
Expected: ALL SCENARIOS PASSED.

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Web/wwwroot/app.js tests/client/app.harness.mjs
git commit -m "feat: let a file slot hold a past run's stored file"
```

---

### Task 10: "Run again" opens the Files step

**Files:**
- Modify: `src/HazardRecon.Web/wwwroot/app.js` (`:1315`), `src/HazardRecon.Web/wwwroot/index.html` (the Files step, near `#sets`)
- Test: `tests/client/app.harness.mjs`

**Interfaces:**
- Consumes: `adoptStoredInputs` (Task 9).
- Produces: `rerunFromDetail()` — the `#btn-rerun` handler.

- [ ] **Step 1: Write the failing test**

Add to `tests/client/app.harness.mjs` and register after `scenarioDUU`:

```js
/* ---------------- DV: Run again opens the files, it does not start a run ---------------- */
async function scenarioDV() {
  console.log("DV) Run again returns to the Files step with the stored files shown");
  let runCalls = 0;
  const h = bootAuth({ access_token: "tok-abc" }, (url) => {
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (url === "/api/run") { runCalls++; return Promise.resolve(jsonRes(200, { status: "running" })); }
    if (/\/inputs$/.test(url)) return Promise.resolve(jsonRes(200, {
      inputs_purged: false,
      sets: [{ index: 0, label: "JUNE 2026", files: [
        { role: "exposure", name: "IFRS9 FILE JUNE 2025.csv", size_bytes: 12582912 },
        { role: "writeoff", name: "2026_WRITEOFF.csv", size_bytes: 9437184 },
      ] }],
    }));
    return Promise.resolve(jsonRes(200, []));
  });
  await tick(); await tick(); await tick();

  h.ctx.showInventory(INVENTORY_FIX);
  h.ctx.showResults(DETAIL_RESULT, []);
  h.$get("#btn-rerun")._fire("click");
  for (let i = 0; i < 6; i++) await tick();

  check("no run was started", runCalls === 0, `run calls=${runCalls}`);
  check("the wizard is showing", !h.$get("#screen-wizard").classList.contains("hide"));
  check("the detail screen is left", h.$get("#screen-detail").classList.contains("hide"));
  check("it lands on the files step", !h.$get("#step-files").classList.contains("hide"));
  check("the title is step 1", /Choose your input files/.test(h.$get("#step-title").textContent),
    `title='${h.$get("#step-title").textContent}'`);
  check("the stored exposure file is named",
    /IFRS9 FILE JUNE 2025\.csv/.test(slotSubText(h, 0, "exposure")), `sub='${slotSubText(h, 0, "exposure")}'`);
  check("the stored write-off file is named",
    /2026_WRITEOFF\.csv/.test(slotSubText(h, 0, "writeoff")), `sub='${slotSubText(h, 0, "writeoff")}'`);
  check("check columns is offered", h.$get("#btn-check").disabled === false);
  check("no expiry notice is shown", h.$get("#files-expired").classList.contains("hide"));

  // splitting the two buttons must not stop the confirm step starting a run
  h.$get("#btn-run")._fire("click");
  for (let i = 0; i < 4; i++) await tick();
  check("Run reconciliation still starts a run", runCalls === 1, `run calls=${runCalls}`);
}

/* ---------------- DW: Run again on a purged run ---------------- */
async function scenarioDW() {
  console.log("DW) Run again on a run whose inputs expired asks for them again");
  const h = bootAuth({ access_token: "tok-abc" }, (url) => {
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (/\/inputs$/.test(url))
      return Promise.resolve(jsonRes(200, { inputs_purged: true, sets: [] }));
    return Promise.resolve(jsonRes(200, []));
  });
  await tick(); await tick(); await tick();

  h.ctx.showInventory(INVENTORY_FIX);
  h.ctx.showResults(DETAIL_RESULT, []);
  h.$get("#btn-rerun")._fire("click");
  for (let i = 0; i < 6; i++) await tick();

  check("it still lands on the files step", !h.$get("#step-files").classList.contains("hide"));
  check("the expiry is explained", !h.$get("#files-expired").classList.contains("hide"));
  check("it says how long they are kept", /30 days/.test(h.$get("#files-expired").textContent),
    `text='${h.$get("#files-expired").textContent}'`);
  check("nothing is preselected", h.$get("#btn-check").disabled === true);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `node tests/client/app.harness.mjs`
Expected: FAIL — "no run was started" fails (the shared handler starts one) and `#files-expired` is not hidden, because it does not exist.

- [ ] **Step 3: Add the notice to the markup**

In `index.html`, inside the Files step just above the `#sets` container:

```html
              <p class="warn hide" id="files-expired" style="margin:0 0 12px">
                The files from that run are no longer stored &mdash; inputs are kept for 30 days.
                Please choose them again.
              </p>
```

- [ ] **Step 4: Give Run again its own handler**

In `app.js`, replace `$("#btn-rerun").addEventListener("click", beginRun);` with:

```js
/* Run again reopens the run's inputs rather than starting a run: the point of it
   is being able to swap one file. The files are fetched from the run rather than
   the page, because a file input cannot be refilled from script and the page may
   have been reloaded since. */
function rerunFromDetail() {
  const from = RUN_ID;
  if (!from) return;

  showScreen("wizard");
  setStep(0);
  RESULT = null;
  setChatOpen(false);
  $("#files-expired").classList.add("hide");

  api("/api/runs/" + from + "/inputs")
    .then(readJson)
    .then(({ ok, j }) => {
      if (!ok || !j) throw new Error((j && j.error) || "Could not read that run's files.");

      if (j.inputs_purged || !(j.sets || []).length) {
        BASED_ON = null;
        SETS = [emptySet()];
        renderSets();
        $("#files-expired").classList.remove("hide");
        return;
      }

      adoptStoredInputs(from, j.sets);
    })
    .catch(e => showError($("#step-files"), e.message));
}

$("#btn-rerun").addEventListener("click", rerunFromDetail);
```

`setStep(0)` also resets the rail, so the later steps are closed until "Check columns" runs — which Task 11 makes conditional.

- [ ] **Step 5: Run the harness to verify it passes**

Run: `node tests/client/app.harness.mjs`
Expected: ALL SCENARIOS PASSED.

- [ ] **Step 6: Commit**

```bash
git add src/HazardRecon.Web/wwwroot/app.js src/HazardRecon.Web/wwwroot/index.html tests/client/app.harness.mjs
git commit -m "feat: Run again reopens the run's files instead of starting a run"
```

---

### Task 11: A changed file closes the later steps

Landing on Files with the previous run's mapping and confirmation still reachable is what makes "change nothing, just re-run" cost no upload. That is only safe while the files on screen are the files the server holds.

**Files:**
- Modify: `src/HazardRecon.Web/wwwroot/app.js` (`setStep` ~`:207`, `renderSets` slot handlers ~`:617`/`:635`, `rerunFromDetail`)
- Test: `tests/client/app.harness.mjs`

**Interfaces:**
- Produces: `filesChanged()` — closes every step after Files.

- [ ] **Step 1: Write the failing test**

Add and register after `scenarioDW`:

```js
/* ---------------- DX: editing a file closes the steps behind it ---------------- */
async function scenarioDX() {
  console.log("DX) an unchanged re-run can go straight on; a changed file forces a re-check");
  const h = bootAuth({ access_token: "tok-abc" }, (url) => {
    if (url === "/api/config") return Promise.resolve(jsonRes(200, CFG));
    if (/\/inputs$/.test(url)) return Promise.resolve(jsonRes(200, {
      inputs_purged: false,
      sets: [{ index: 0, label: "JUNE", files: [
        { role: "exposure", name: "e.csv", size_bytes: 10 },
        { role: "debug", name: "d.zip", size_bytes: 10 },
      ] }],
    }));
    return Promise.resolve(jsonRes(200, []));
  });
  await tick(); await tick(); await tick();

  // a completed run: every step has been reached
  h.ctx.showInventory(INVENTORY_FIX);
  h.ctx.showResults(DETAIL_RESULT, []);
  h.$get("#btn-rerun")._fire("click");
  for (let i = 0; i < 6; i++) await tick();

  check("the confirm step is still reachable", h.$get("#rail-2").disabled === false,
    "an unchanged re-run should not have to upload again");

  // replacing a file invalidates everything discovery worked out
  pickInto(h, 0, "exposure", [mkFile("new-e.csv", 10)]);

  check("the mapping step closes", h.$get("#rail-1").disabled === true);
  check("the confirm step closes", h.$get("#rail-2").disabled === true);
  check("the run step closes", h.$get("#rail-3").disabled === true);
  check("the files step is still where we are", !h.$get("#step-files").classList.contains("hide"));

  // and clearing one counts as a change too
  const h2 = h;
  check("check columns is still offered", h2.$get("#btn-check").disabled === false);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `node tests/client/app.harness.mjs`
Expected: FAIL on "the confirm step is still reachable" — `setStep(0)` from Task 10 leaves `STEP_REACHED` at 0, so the rail closes everything. The three "closes" checks pass for the wrong reason at this point; step 3 makes them pass for the right one.

- [ ] **Step 3: Add the invalidation**

In `app.js`, next to `setStep`:

```js
/* Everything after the files - the mapping, the inventory, the run - describes
   the files as they were uploaded. Changing one makes all of it stale, so the
   rail closes behind this step and "Check columns" has to run again. Without
   this, replacing a file and stepping forward would run the previous upload
   while the screen showed the new file. */
function filesChanged() {
  STEP_REACHED = STEP_AT;
  setStep(STEP_AT);
}
```

Call it from both places a slot is edited, in `renderSets`:

```js
      input.addEventListener("change", () => {
        if (input.files && input.files.length) {
          set.files[kind.key] = Array.from(input.files);
          filesChanged();
          renderSets();
        }
      });
```

```js
        clear.addEventListener("click", () => {
          set.files[kind.key] = [];
          filesChanged();
          renderSets();
        });
```

Also call it from the set-level drop button and "Add another set", since both change what will be uploaded:

```js
    drop.addEventListener("click", () => {
      if (SETS.length > 1) SETS.splice(idx, 1);
      else SETS[idx] = emptySet();
      filesChanged();
      renderSets();
    });
```

Then make `rerunFromDetail` keep the reached steps rather than resetting them. Give `setStep` an optional argument, so there is one way to express this rather than a caller nudging the state back afterwards:

```js
function setStep(n, keepReached = false) {
  STEP_AT = n;
  if (n > STEP_REACHED) STEP_REACHED = n;
  else if (!keepReached) STEP_REACHED = n;
  ...
```

Careful: the existing first line is `if (n > STEP_REACHED) STEP_REACHED = n;` and nothing lowers it, because until now every backward move was a rail click that *should* keep the forward steps open. Lowering it by default would break that, so leave the existing behaviour alone and only lower it where it is asked for:

```js
function setStep(n) {
  STEP_AT = n;
  if (n > STEP_REACHED) STEP_REACHED = n;
  // ... unchanged
}
```

and have `filesChanged()` do the lowering, which it already does (`STEP_REACHED = STEP_AT`). Then `rerunFromDetail`'s plain `setStep(0)` is already correct — it does not lower `STEP_REACHED`, so a completed run's steps stay reachable, and the first edit closes them.

That means **no change is needed here at all** beyond `filesChanged()` and its call sites. Verify that is true by running the harness; if "the confirm step is still reachable" still fails, the cause is `showResults`/`resetWizard` zeroing `STEP_REACHED` on the way to the detail screen — find which, and stop it doing so for the re-run path only.

- [ ] **Step 4: Run the harness to verify it passes**

Run: `node tests/client/app.harness.mjs`
Expected: ALL SCENARIOS PASSED.

- [ ] **Step 5: Check the earlier rail scenario still holds**

Scenario Y asserts a fresh run cannot jump ahead. Confirm it still passes — `resetWizard` must still zero `STEP_REACHED`.

Run: `node tests/client/app.harness.mjs 2>&1 | sed -n '/^Y)/,/^YY)/p'`
Expected: every check PASS.

- [ ] **Step 6: Run both suites and commit**

```bash
dotnet test tests/HazardRecon.Tests
node tests/client/app.harness.mjs
git add src/HazardRecon.Web/wwwroot/app.js tests/client/app.harness.mjs
git commit -m "fix: close the steps behind a file that changed"
```

---

## Manual verification

After Task 11, verify end to end against real files, since no automated test uploads to real storage:

1. Start the web app, sign in, and run a set through to results.
2. Click **Run again** — the Files step should show all four files with their original names and sizes.
3. Press **Check columns** without changing anything, confirm the mapping, and run. It should complete.
4. Click **Run again**, press **Replace** on the write-off file only, and pick a different CSV. The rail's later steps should close.
5. Press **Check columns**. Watch the network tab: only the write-off file should be in the request body, alongside `based_on_run` and `reuse`.
6. Confirm the run completes and the summary reports a trace rate, not 0%.
