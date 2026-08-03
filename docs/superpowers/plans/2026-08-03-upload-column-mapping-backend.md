# Upload Column Mapping (Backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let write-off and IFRS9 CSVs with non-standard (or missing) headers be reconciled correctly, by resolving their columns through a user-confirmed, AI-assisted mapping applied server-side, and by replacing the folder-picker upload with four explicit per-set files.

**Architecture:** New tables (`saved_column_mappings`, `run_set_column_mappings`) back a reusable, column-signature-keyed mapping. `DataLoaders`/`ReconciliationEngine` gain an optional `ColumnMap` that resolves a field name to its real column (name or index) instead of a hardcoded literal — a `null` map reproduces today's exact behavior, so the CLI is untouched. The web upload becomes two round trips: `POST /api/discover` (upload + return column/sample info + a resolved mapping guess) and `POST /api/discover/mapping` (persist the user's confirmed mapping); `/api/run` is unchanged in shape but now threads the confirmed mapping into the engine.

**Tech Stack:** .NET 8/10, CsvHelper, xUnit, Supabase/PostgREST, the existing `ILlmClient` gateway.

## Global Constraints

- The CLI (`HazardRecon.Cli`) must not change and must not need a mapping — every new optional parameter defaults to reproducing today's literal-header-name behavior.
- `saved_column_mappings` is scoped per `user_id` — no cross-user sharing.
- Every new table gets explicit `GRANT`s to `service_role` and `select` to `authenticated` in the same migration that creates it — a prior migration in this repo shipped without these and every request 403'd against a fresh local Supabase instance until fixed; do not repeat that.
- Follow existing conventions exactly: PostgREST access via `SupabaseRestClient`/`FakeHttpMessageHandler` (not Dapper/EF), record POCOs with `[JsonPropertyName]` per `src/HazardRecon.Web/Runs/RunFileRecord.cs`, log kind values from `HazardRecon.Core.Models.LogKind` (never bare string literals).
- This plan covers the backend only (spec's build-order steps 1-6: migration → `DataLoaders` → signature/guessing → `/api/discover` → `/api/discover/mapping` → `/api/run` wiring). The frontend (replacing the folder-picker UI) is a separate follow-up plan once these endpoints' response shapes are settled — see "Deviations from the spec" below for why.

## Deviations from the spec

Two refinements found while planning, both keeping the spec's goals but reducing new code:

1. **`InputDiscoverer` needs no changes at all.** The spec says the web path "bypasses `InputDiscoverer`'s heuristics entirely." Instead, `SetFileReceiver` (Task 9) writes each uploaded file under the exact canonical name `InputDiscoverer.BuildSet` already searches for (`IFRS9.csv`, `writeoff.csv`, `scenario.json`, and the debug file(s) under their real names) — `BuildSet`'s existing pattern matching (`Contains("IFRS9")`, `Contains("WRITEOFF")`, exact `pd_scored.csv`/`debug.json`/`scenario*.json` lookups) finds them without modification, since the names are now guaranteed rather than guessed.
2. **`/api/discover/mapping` does not recompute inventory.** File presence/role is already known the moment `/api/discover` runs (the client tagged every file), so inventory and problems are computed once, in `/api/discover`'s response, alongside the mapping-step data. `/api/discover/mapping` only persists the confirmed mapping and acknowledges — no duplicate discovery work.

## File Structure

- `supabase/migrations/20260803000000_column_mappings.sql` — new tables + RLS + grants.
- `src/HazardRecon.Core/Models/ColumnMap.cs` — new: `ColumnMap`, `SetColumnMaps`.
- `src/HazardRecon.Core/Models/MappableFields.cs` — new: the fixed field lists for `writeoff`/`exposure`.
- `src/HazardRecon.Core/Services/CsvSniffer.cs` — new: header detection + sample rows.
- `src/HazardRecon.Core/Services/ColumnSignature.cs` — new: signature computation.
- `src/HazardRecon.Core/Services/ColumnMappingService.cs` — new: LLM-assisted guessing + resolution order.
- `src/HazardRecon.Core/Services/DataLoaders.cs` — modify: `LoadWriteoff`/`LoadSourceAccounts` gain an optional `ColumnMap?`.
- `src/HazardRecon.Core/Services/ReconciliationEngine.cs` — modify: `Run(...)` gains an optional per-set column-map dictionary.
- `src/HazardRecon.Core/Llm/CyteLlmOptions.cs` — modify: add `MappingModelId`.
- `src/HazardRecon.Web/Runs/SavedColumnMappingRecord.cs`, `RunSetColumnMappingRecord.cs`, `IColumnMappingStore.cs`, `SupabaseColumnMappingStore.cs` — new.
- `src/HazardRecon.Web/Uploads/SetFileReceiver.cs` — new, replaces `UploadReceiver.cs`.
- `src/HazardRecon.Web/Uploads/UploadReceiver.cs`, `UploadPath.cs` — deleted (Task 9).
- `src/HazardRecon.Web/JobState.cs` — modify: stash per-set mappable-file info and confirmed column maps.
- `src/HazardRecon.Web/Program.cs` — modify: DI registrations, `POST /api/discover` redesign, new `POST /api/discover/mapping`, `/api/run` wiring.
- Tests mirror each of the above under `tests/HazardRecon.Tests/` (see per-task `Test:` paths). `tests/HazardRecon.Tests/Web/UploadReceiverTests.cs`, `UploadPathTests.cs` are deleted alongside their sources; `tests/HazardRecon.Tests/Web/UploadEndpointTests.cs` is rewritten in Task 11.

---

### Task 1: Migration — `saved_column_mappings` and `run_set_column_mappings`

**Files:**
- Create: `supabase/migrations/20260803000000_column_mappings.sql`

**Interfaces:**
- Produces: tables `public.saved_column_mappings(id, user_id, file_kind, column_signature, field_name, source_column, created_at, last_used_at)` and `public.run_set_column_mappings(id, run_id, set_key, file_kind, field_name, source_column)`, both with RLS and grants. Later tasks' stores (Task 8) query these exact column names.

There is no automated test for a migration file in this repo (see `supabase/migrations/20260731000000_normalize_run_results.sql` for precedent) — verification is applying it to a real or local Supabase instance.

- [ ] **Step 1: Write the migration**

```sql
-- Saved + per-run column mappings for uploaded write-off/exposure (IFRS9)
-- files. See docs/superpowers/specs/2026-08-03-upload-column-mapping-design.md.

create table public.saved_column_mappings (
  id                bigint generated always as identity primary key,
  user_id           uuid not null references auth.users(id) on delete cascade,
  file_kind         text not null check (file_kind in ('writeoff', 'exposure')),
  column_signature  text not null,
  field_name        text not null,
  source_column     text not null,
  created_at        timestamptz not null default now(),
  last_used_at      timestamptz not null default now(),
  unique (user_id, file_kind, column_signature, field_name)
);

create index saved_column_mappings_lookup_idx
  on public.saved_column_mappings (user_id, file_kind, column_signature);

create table public.run_set_column_mappings (
  id             bigint generated always as identity primary key,
  run_id         uuid not null references public.runs(id) on delete cascade,
  set_key        text not null,
  file_kind      text not null check (file_kind in ('writeoff', 'exposure')),
  field_name     text not null,
  source_column  text not null,
  unique (run_id, set_key, file_kind, field_name)
);

alter table public.saved_column_mappings enable row level security;
alter table public.run_set_column_mappings enable row level security;

create policy "own saved column mappings readable" on public.saved_column_mappings
  for select to authenticated using (auth.uid() = user_id);

create policy "own run set column mappings readable" on public.run_set_column_mappings
  for select to authenticated using (
    exists (select 1 from public.runs r where r.id = run_set_column_mappings.run_id and r.user_id = auth.uid())
  );

grant select, insert, update, delete on public.saved_column_mappings to service_role;
grant select, insert, update, delete on public.run_set_column_mappings to service_role;
grant select on public.saved_column_mappings to authenticated;
grant select on public.run_set_column_mappings to authenticated;
```

- [ ] **Step 2: Apply it to a local Supabase instance and verify**

Run: `npx supabase@latest db reset` (from the repo root; requires `supabase start` to have been run once — see this session's earlier local-Supabase setup for exact ports/keys if a fresh stack is needed).
Expected: `Applying migration 20260803000000_column_mappings.sql...` with no error, followed by `Finished supabase db reset`.

Then verify grants landed (this is exactly the check that caught the missing-grants bug last time):
Run: `docker exec -i supabase_db_hazard-rate-recon-dotnet psql -U postgres -d postgres -c "\dp public.saved_column_mappings"`
Expected: the `Access privileges` column shows `service_role=arwdDxtm/postgres` and `authenticated=rDxtm/postgres` (not just `Dxtm`).

- [ ] **Step 3: Commit**

```bash
git add supabase/migrations/20260803000000_column_mappings.sql
git commit -m "feat: add saved and per-run column mapping tables"
```

---

### Task 2: `ColumnMap` and `SetColumnMaps` models

**Files:**
- Create: `src/HazardRecon.Core/Models/ColumnMap.cs`
- Test: `tests/HazardRecon.Tests/ColumnMapTests.cs`

**Interfaces:**
- Produces: `ColumnMap(bool hasHeaders, IReadOnlyDictionary<string,string> sourceColumns)` with `.HasHeaders` and `.Resolve(string field) : string`; `SetColumnMaps(ColumnMap? WriteOff, ColumnMap? Exposure)`. Task 6 (`DataLoaders`) and Task 7 (`ReconciliationEngine`) consume both types exactly as named here.

- [ ] **Step 1: Write the failing test**

```csharp
using HazardRecon.Core.Models;
using Xunit;

namespace HazardRecon.Tests;

public class ColumnMapTests
{
    [Fact]
    public void TestResolveReturnsTheMappedColumnWhenPresent()
    {
        ColumnMap map = new(hasHeaders: true, new Dictionary<string, string> { ["LoanAccountNumber"] = "Column 1" });

        Assert.Equal("Column 1", map.Resolve("LoanAccountNumber"));
    }

    [Fact]
    public void TestResolveFallsBackToTheFieldNameWhenNotMapped()
    {
        ColumnMap map = new(hasHeaders: true, new Dictionary<string, string>());

        Assert.Equal("Amount", map.Resolve("Amount"));
    }

    [Fact]
    public void TestHasHeadersIsExposed()
    {
        ColumnMap headerless = new(hasHeaders: false, new Dictionary<string, string>());

        Assert.False(headerless.HasHeaders);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ColumnMapTests`
Expected: FAIL — `ColumnMap` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

```csharp
namespace HazardRecon.Core.Models;

/// <summary>
/// Resolves a field name to where it actually lives in a CSV: a header name if
/// the file has one, or a stringified 0-based column index if it does not. A
/// field with no entry resolves to its own name - today's literal-header-name
/// behavior, which is what a caller passing no map at all gets by default.
/// </summary>
public class ColumnMap
{
    public bool HasHeaders { get; }
    private readonly IReadOnlyDictionary<string, string> _sourceColumns;

    public ColumnMap(bool hasHeaders, IReadOnlyDictionary<string, string> sourceColumns)
    {
        HasHeaders = hasHeaders;
        _sourceColumns = sourceColumns;
    }

    public string Resolve(string field) =>
        _sourceColumns.TryGetValue(field, out string? column) ? column : field;
}

/// <summary>The two mappable files for one set - either may be null (no mapping confirmed, or the CLI path).</summary>
public record SetColumnMaps(ColumnMap? WriteOff, ColumnMap? Exposure);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ColumnMapTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Core/Models/ColumnMap.cs tests/HazardRecon.Tests/ColumnMapTests.cs
git commit -m "feat: add ColumnMap for resolving mapped CSV columns"
```

---

### Task 3: `MappableFields` — the fixed field lists

**Files:**
- Create: `src/HazardRecon.Core/Models/MappableFields.cs`
- Test: `tests/HazardRecon.Tests/MappableFieldsTests.cs`

**Interfaces:**
- Produces: `MappingFieldSpec(string Field, string Note)`; `MappableFields.Writeoff` and `MappableFields.Exposure`, each `IReadOnlyList<MappingFieldSpec>`. Task 5 (`ColumnMappingService`) and Task 11 (`/api/discover`) consume these exact lists so the mapping response always advertises the same fields.

- [ ] **Step 1: Write the failing test**

```csharp
using HazardRecon.Core.Models;
using Xunit;

namespace HazardRecon.Tests;

public class MappableFieldsTests
{
    [Fact]
    public void TestWriteoffListsAllFourFields()
    {
        Assert.Equal(
            new[] { "LoanAccountNumber", "CustomerId", "Amount", "ReportDate" },
            MappableFields.Writeoff.Select(f => f.Field));
    }

    [Fact]
    public void TestExposureListsBothFields()
    {
        Assert.Equal(
            new[] { "LoanAccountNumber", "AmountOutstanding" },
            MappableFields.Exposure.Select(f => f.Field));
    }

    [Fact]
    public void TestEveryFieldHasANonEmptyNote()
    {
        Assert.All(MappableFields.Writeoff.Concat(MappableFields.Exposure),
            f => Assert.False(string.IsNullOrWhiteSpace(f.Note)));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter MappableFieldsTests`
Expected: FAIL — `MappableFields` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
namespace HazardRecon.Core.Models;

/// <summary>One field the mapping step needs a column for, with the explanation shown next to it.</summary>
public record MappingFieldSpec(string Field, string Note);

/// <summary>
/// The fixed fields the engine reads from the write-off and exposure (IFRS9)
/// files - see DataLoaders.LoadWriteoff/LoadSourceAccounts for where each is
/// actually consumed.
/// </summary>
public static class MappableFields
{
    public static readonly IReadOnlyList<MappingFieldSpec> Writeoff = new[]
    {
        new MappingFieldSpec("LoanAccountNumber", "Normalised and used as the join key against defaults and exposure"),
        new MappingFieldSpec("CustomerId", "Carried through - not used for matching logic"),
        new MappingFieldSpec("Amount", "Summed per account into the write-off exposure"),
        new MappingFieldSpec("ReportDate", "Classifies each write-off as pre-, in- or post-window")
    };

    public static readonly IReadOnlyList<MappingFieldSpec> Exposure = new[]
    {
        new MappingFieldSpec("LoanAccountNumber", "Join key - Check 1 traces defaults into this population"),
        new MappingFieldSpec("AmountOutstanding", "Summed per account for the exposure figure")
    };
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter MappableFieldsTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Core/Models/MappableFields.cs tests/HazardRecon.Tests/MappableFieldsTests.cs
git commit -m "feat: add the fixed mappable-field lists for write-off and exposure"
```

---

### Task 4: `CsvSniffer` — header detection and sample rows

**Files:**
- Create: `src/HazardRecon.Core/Services/CsvSniffer.cs`
- Test: `tests/HazardRecon.Tests/CsvSnifferTests.cs`

**Interfaces:**
- Consumes: nothing new (reads a file path with `CsvHelper`, already a project dependency).
- Produces: `CsvSniff(bool HasHeaders, IReadOnlyList<string>? Headers, IReadOnlyList<IReadOnlyList<string>> SampleRows)`; `CsvSniffer.Sniff(string path, int sampleRowCount = 5) : CsvSniff`. Task 5 (`ColumnMappingService.Resolve`) and Task 11 (`/api/discover`) consume this exact shape.

- [ ] **Step 1: Write the failing test**

```csharp
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class CsvSnifferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hr-sniffer-tests", Guid.NewGuid().ToString("N")[..8]);

    public CsvSnifferTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string content)
    {
        string path = Path.Combine(_dir, "in.csv");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TestAFileWithTextHeadersIsDetectedAsHeadered()
    {
        string path = WriteFile("LoanAccountNumber,Amount,ReportDate\nA1,100,2026-04-30\nA2,200,2026-05-01\n");

        CsvSniff sniff = CsvSniffer.Sniff(path);

        Assert.True(sniff.HasHeaders);
        Assert.Equal(new[] { "LoanAccountNumber", "Amount", "ReportDate" }, sniff.Headers);
        Assert.Equal(2, sniff.SampleRows.Count);
        Assert.Equal("A1", sniff.SampleRows[0][0]);
    }

    [Fact]
    public void TestAFileWithNoHeaderRowIsDetectedAsHeaderless()
    {
        string path = WriteFile("A1,100,2026-04-30\nA2,200,2026-05-01\nA3,300,2026-05-02\n");

        CsvSniff sniff = CsvSniffer.Sniff(path);

        Assert.False(sniff.HasHeaders);
        Assert.Null(sniff.Headers);
        Assert.Equal(3, sniff.SampleRows.Count);
        Assert.Equal("A1", sniff.SampleRows[0][0]);
    }

    [Fact]
    public void TestSampleRowsAreCappedAtTheRequestedCount()
    {
        string content = string.Join("\n", Enumerable.Range(0, 20).Select(i => $"A{i},{i * 10},2026-01-0{(i % 9) + 1}")) + "\n";
        string path = WriteFile(content);

        CsvSniff sniff = CsvSniffer.Sniff(path, sampleRowCount: 3);

        Assert.Equal(3, sniff.SampleRows.Count);
    }

    [Fact]
    public void TestAnEmptyFileHasNoHeadersAndNoSamples()
    {
        string path = WriteFile("");

        CsvSniff sniff = CsvSniffer.Sniff(path);

        Assert.False(sniff.HasHeaders);
        Assert.Empty(sniff.SampleRows);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter CsvSnifferTests`
Expected: FAIL — `CsvSniffer`/`CsvSniff` do not exist.

- [ ] **Step 3: Write the implementation**

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter CsvSnifferTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Core/Services/CsvSniffer.cs tests/HazardRecon.Tests/CsvSnifferTests.cs
git commit -m "feat: add CsvSniffer to detect header rows and sample data"
```

---

### Task 5: `ColumnSignature` — fingerprint a file's column shape

**Files:**
- Create: `src/HazardRecon.Core/Services/ColumnSignature.cs`
- Test: `tests/HazardRecon.Tests/ColumnSignatureTests.cs`

**Interfaces:**
- Consumes: the same `(headers, sampleRows)` shape `CsvSniff` produces (Task 4), passed as plain parameters so this has no hard type dependency on `CsvSniff`.
- Produces: `ColumnSignature.Compute(IReadOnlyList<string>? headers, IReadOnlyList<IReadOnlyList<string>> sampleRows) : string`. Task 11 (`/api/discover`) consumes this to look up and save mappings.

- [ ] **Step 1: Write the failing test**

```csharp
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class ColumnSignatureTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Rows(params string[][] rows) =>
        rows.Select(r => (IReadOnlyList<string>)r).ToList();

    [Fact]
    public void TestSameHeadersProduceTheSameSignatureRegardlessOfCase()
    {
        string a = ColumnSignature.Compute(new[] { "LoanAccountNumber", "Amount" }, Rows());
        string b = ColumnSignature.Compute(new[] { "loanaccountnumber", "AMOUNT" }, Rows());

        Assert.Equal(a, b);
    }

    [Fact]
    public void TestDifferentHeaderOrderProducesADifferentSignature()
    {
        string a = ColumnSignature.Compute(new[] { "LoanAccountNumber", "Amount" }, Rows());
        string b = ColumnSignature.Compute(new[] { "Amount", "LoanAccountNumber" }, Rows());

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TestHeaderlessFilesWithTheSameColumnShapesMatch()
    {
        string a = ColumnSignature.Compute(null, Rows(
            new[] { "A1", "2026-04-30", "100.50" },
            new[] { "A2", "2026-05-01", "200.75" }));

        string b = ColumnSignature.Compute(null, Rows(
            new[] { "B9", "2026-06-15", "999.00" },
            new[] { "B8", "2026-06-16", "1.00" }));

        Assert.Equal(a, b);
    }

    [Fact]
    public void TestHeaderlessFilesWithDifferentColumnShapesDoNotMatch()
    {
        string numericThenDate = ColumnSignature.Compute(null, Rows(new[] { "100", "2026-04-30" }));
        string dateThenNumeric = ColumnSignature.Compute(null, Rows(new[] { "2026-04-30", "100" }));

        Assert.NotEqual(numericThenDate, dateThenNumeric);
    }

    [Fact]
    public void TestAHeaderedSignatureNeverMatchesAHeaderlessOne()
    {
        string headered = ColumnSignature.Compute(new[] { "A", "B" }, Rows());
        string headerless = ColumnSignature.Compute(null, Rows(new[] { "A", "B" }));

        Assert.NotEqual(headered, headerless);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ColumnSignatureTests`
Expected: FAIL — `ColumnSignature` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ColumnSignatureTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Core/Services/ColumnSignature.cs tests/HazardRecon.Tests/ColumnSignatureTests.cs
git commit -m "feat: add ColumnSignature to fingerprint a CSV's column shape"
```

---

### Task 6: `ColumnMappingService` — AI-assisted guessing and resolution order

**Files:**
- Create: `src/HazardRecon.Core/Services/ColumnMappingService.cs`
- Test: `tests/HazardRecon.Tests/ColumnMappingServiceTests.cs`

**Interfaces:**
- Consumes: `ILlmClient`/`LlmMessage`/`LlmChatResult` (existing, `HazardRecon.Core.Llm`); `MappingFieldSpec`, `MappableFields` (Task 3); `LogKind` (existing).
- Produces: `MappingGuess(string? Column, double? Confidence)`; `ResolvedField(string Field, string? Column, double? Confidence, string Source)` where `Source` is one of `"header_match"`, `"saved"`, `"ai_guess"`, `"unmapped"`; `ColumnMappingService(ILlmClient client, string modelId)` with `.Resolve(IReadOnlyList<string>? headers, IReadOnlyList<IReadOnlyList<string>> sampleRows, IReadOnlyList<MappingFieldSpec> fields, IReadOnlyDictionary<string,string>? savedMapping, Action<string,string>? log = null) : IReadOnlyList<ResolvedField>`. Task 11 (`/api/discover`) is the only consumer.

- [ ] **Step 1: Write the failing test**

```csharp
using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using HazardRecon.Tests.Llm;
using Xunit;

namespace HazardRecon.Tests;

public class ColumnMappingServiceTests
{
    private static readonly IReadOnlyList<MappingFieldSpec> Fields = MappableFields.Exposure;

    [Fact]
    public void TestAnExactHeaderMatchNeedsNoAiCall()
    {
        FakeLlmClient client = new();
        ColumnMappingService service = new(client, "model-1");

        var resolved = service.Resolve(
            headers: new[] { "LoanAccountNumber", "AmountOutstanding" },
            sampleRows: new List<IReadOnlyList<string>>(),
            fields: Fields,
            savedMapping: null);

        Assert.All(resolved, r => Assert.Equal("header_match", r.Source));
        Assert.Equal(0, client.ChatCalls);
    }

    [Fact]
    public void TestASavedMappingIsUsedBeforeAskingTheAi()
    {
        FakeLlmClient client = new();
        ColumnMappingService service = new(client, "model-1");
        var saved = new Dictionary<string, string> { ["LoanAccountNumber"] = "Column 1", ["AmountOutstanding"] = "Column 3" };

        var resolved = service.Resolve(
            headers: null,
            sampleRows: new List<IReadOnlyList<string>> { new[] { "A1", "100" } },
            fields: Fields,
            savedMapping: saved);

        Assert.All(resolved, r => Assert.Equal("saved", r.Source));
        Assert.Equal(0, client.ChatCalls);
    }

    [Fact]
    public void TestAnUnmatchedFieldFallsBackToAnAiGuess()
    {
        FakeLlmClient client = new()
        {
            ReplyContent = """{"LoanAccountNumber": {"column": "Column 1", "confidence": 0.97}, "AmountOutstanding": {"column": "Column 3", "confidence": 0.88}}"""
        };
        ColumnMappingService service = new(client, "model-1");

        var resolved = service.Resolve(
            headers: null,
            sampleRows: new List<IReadOnlyList<string>> { new[] { "A1", "2026-06-30", "100", "Stage 2" } },
            fields: Fields,
            savedMapping: null);

        var byField = resolved.ToDictionary(r => r.Field);
        Assert.Equal("Column 1", byField["LoanAccountNumber"].Column);
        Assert.Equal(0.97, byField["LoanAccountNumber"].Confidence);
        Assert.Equal("ai_guess", byField["LoanAccountNumber"].Source);
        Assert.Equal(1, client.ChatCalls);
    }

    [Fact]
    public void TestAFieldTheAiCannotGuessComesBackUnmapped()
    {
        FakeLlmClient client = new() { ReplyContent = """{"LoanAccountNumber": {"column": "Column 1", "confidence": 0.9}}""" };
        ColumnMappingService service = new(client, "model-1");

        var resolved = service.Resolve(null, new List<IReadOnlyList<string>>(), Fields, null);

        var amountOutstanding = resolved.Single(r => r.Field == "AmountOutstanding");
        Assert.Equal("unmapped", amountOutstanding.Source);
        Assert.Null(amountOutstanding.Column);
    }

    [Fact]
    public void TestAThrownExceptionDegradesToUnmappedRatherThanThrowing()
    {
        FakeLlmClient client = new() { ThrowOnChat = new LlmException("gateway down") };
        ColumnMappingService service = new(client, "model-1");

        var resolved = service.Resolve(null, new List<IReadOnlyList<string>>(), Fields, null);

        Assert.All(resolved, r => Assert.Equal("unmapped", r.Source));
    }

    [Fact]
    public void TestUnparseableJsonDegradesToUnmappedRatherThanThrowing()
    {
        FakeLlmClient client = new() { ReplyContent = "not json at all" };
        ColumnMappingService service = new(client, "model-1");

        var resolved = service.Resolve(null, new List<IReadOnlyList<string>>(), Fields, null);

        Assert.All(resolved, r => Assert.Equal("unmapped", r.Source));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ColumnMappingServiceTests`
Expected: FAIL — `ColumnMappingService` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Text.Json;
using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;

namespace HazardRecon.Core.Services;

/// <summary>An AI-guessed column for one field, or none if the AI could not tell.</summary>
public record MappingGuess(string? Column, double? Confidence);

/// <summary>
/// One field resolved to a column (or not). Source is "header_match", "saved",
/// "ai_guess" or "unmapped", in the order those are tried.
/// </summary>
public record ResolvedField(string Field, string? Column, double? Confidence, string Source);

/// <summary>
/// Resolves each mappable field to a column in an uploaded file: an exact
/// header match first, then a previously saved mapping for this column
/// signature, then an AI guess from the header/sample data, then unmapped.
/// The AI call mirrors AiAnalysisService's defensive shape - any failure or
/// unparseable reply just means no guess, never blocks the caller.
/// </summary>
public class ColumnMappingService
{
    private const string SystemPrompt = @"You are matching columns in an uploaded CSV to a fixed set of
required fields for a credit-risk reconciliation tool. Given the file's header
row (if any) and a few sample rows, return ONLY a JSON object - no prose, no
markdown fences - mapping each required field name to the best-matching column
identifier (the header name if the file has headers, or a 0-based column index
as a string if it does not) and a confidence between 0 and 1. If no column
plausibly matches a field, omit that field entirely. Example shape:
{""FieldName"": {""column"": ""ColumnNameOrIndex"", ""confidence"": 0.97}}";

    private readonly ILlmClient _client;
    private readonly string _modelId;

    public ColumnMappingService(ILlmClient client, string modelId)
    {
        _client = client;
        _modelId = modelId;
    }

    public IReadOnlyList<ResolvedField> Resolve(
        IReadOnlyList<string>? headers,
        IReadOnlyList<IReadOnlyList<string>> sampleRows,
        IReadOnlyList<MappingFieldSpec> fields,
        IReadOnlyDictionary<string, string>? savedMapping,
        Action<string, string>? log = null)
    {
        List<ResolvedField> resolved = new();
        List<MappingFieldSpec> needsGuess = new();

        foreach (MappingFieldSpec field in fields)
        {
            string? exact = headers?.FirstOrDefault(h => string.Equals(h, field.Field, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                resolved.Add(new ResolvedField(field.Field, exact, null, "header_match"));
                continue;
            }

            if (savedMapping != null && savedMapping.TryGetValue(field.Field, out string? savedColumn))
            {
                resolved.Add(new ResolvedField(field.Field, savedColumn, null, "saved"));
                continue;
            }

            needsGuess.Add(field);
        }

        if (needsGuess.Count > 0)
        {
            Dictionary<string, MappingGuess> guesses = Guess(headers, sampleRows, needsGuess, log);
            foreach (MappingFieldSpec field in needsGuess)
            {
                resolved.Add(guesses.TryGetValue(field.Field, out MappingGuess? g) && g.Column != null
                    ? new ResolvedField(field.Field, g.Column, g.Confidence, "ai_guess")
                    : new ResolvedField(field.Field, null, null, "unmapped"));
            }
        }

        return resolved;
    }

    private Dictionary<string, MappingGuess> Guess(
        IReadOnlyList<string>? headers,
        IReadOnlyList<IReadOnlyList<string>> sampleRows,
        IReadOnlyList<MappingFieldSpec> fields,
        Action<string, string>? log)
    {
        Dictionary<string, MappingGuess> result = new();

        try
        {
            string columnsDescription = headers != null
                ? "Header row: " + string.Join(", ", headers)
                : "No header row. Columns are 0-based index 0.." + Math.Max(0, (sampleRows.FirstOrDefault()?.Count ?? 1) - 1) + ".";

            string samplesText = string.Join("\n", sampleRows.Take(5).Select(r => string.Join(" | ", r)));
            string fieldsText = string.Join("\n", fields.Select(f => $"- {f.Field}: {f.Note}"));

            List<LlmMessage> messages = new()
            {
                new LlmMessage("system", SystemPrompt),
                new LlmMessage("user", $"{columnsDescription}\n\nSample rows:\n{samplesText}\n\nRequired fields:\n{fieldsText}")
            };

            LlmChatResult res = _client.ChatAsync(_modelId, messages).GetAwaiter().GetResult();
            string content = (res.Content ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(content))
            {
                log?.Invoke("Column mapping: AI returned no content", LogKind.Warn);
                return result;
            }

            using JsonDocument doc = JsonDocument.Parse(content);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;

                string? column = prop.Value.TryGetProperty("column", out JsonElement c) ? c.GetString() : null;
                double? confidence = prop.Value.TryGetProperty("confidence", out JsonElement conf) && conf.ValueKind == JsonValueKind.Number
                    ? conf.GetDouble()
                    : null;

                if (!string.IsNullOrEmpty(column))
                {
                    result[prop.Name] = new MappingGuess(column, confidence);
                }
            }

            log?.Invoke($"Column mapping: AI guessed {result.Count} of {fields.Count} field(s)", LogKind.Ok);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Column mapping: AI unavailable: {ex.GetType().Name}: {ex.Message}", LogKind.Warn);
        }

        return result;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ColumnMappingServiceTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Core/Services/ColumnMappingService.cs tests/HazardRecon.Tests/ColumnMappingServiceTests.cs
git commit -m "feat: add ColumnMappingService for AI-assisted column guessing"
```

---

### Task 7: `DataLoaders` — resolve columns through an optional `ColumnMap`

**Files:**
- Modify: `src/HazardRecon.Core/Services/DataLoaders.cs`
- Test: `tests/HazardRecon.Tests/DataLoadersMappingTests.cs`

**Interfaces:**
- Consumes: `ColumnMap` (Task 2).
- Produces: `LoadWriteoff(string? path, Action<string,string>? log = null, ColumnMap? columnMap = null)` and `LoadSourceAccounts(string? path, string colName, string label, string? amountCol = null, Action<string,string>? log = null, ColumnMap? columnMap = null)` — same return types as today, new trailing optional parameter only. Task 8 (`ReconciliationEngine`) is the only other caller besides existing tests.

- [ ] **Step 1: Write the failing tests**

```csharp
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class DataLoadersMappingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hr-dataloaders-mapping-tests", Guid.NewGuid().ToString("N")[..8]);
    private readonly DataLoaders _loaders = new();

    public DataLoadersMappingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void TestLoadWriteoffWithNoMapUsesLiteralHeaderNamesLikeToday()
    {
        string path = WriteFile("wo.csv", "LoanAccountNumber,CustomerId,Amount,ReportDate\nA1,C1,100,2026-04-30\n");

        var (agg, accts) = _loaders.LoadWriteoff(path);

        Assert.Contains("A1", accts);
        Assert.Equal(100, agg[0].WriteOffAmount);
    }

    [Fact]
    public void TestLoadWriteoffWithAHeaderedMapResolvesRenamedColumns()
    {
        string path = WriteFile("wo.csv", "AcctNo,Cust,Amt,Dt\nA1,C1,250.5,2026-05-01\n");
        ColumnMap map = new(hasHeaders: true, new Dictionary<string, string>
        {
            ["LoanAccountNumber"] = "AcctNo", ["CustomerId"] = "Cust", ["Amount"] = "Amt", ["ReportDate"] = "Dt"
        });

        var (agg, accts) = _loaders.LoadWriteoff(path, columnMap: map);

        Assert.Contains("A1", accts);
        Assert.Equal(250.5, agg[0].WriteOffAmount);
        Assert.Equal("C1", agg[0].CustomerId);
    }

    [Fact]
    public void TestLoadWriteoffWithAHeaderlessMapResolvesByIndex()
    {
        string path = WriteFile("wo.csv", "A1,C1,300,2026-05-02\n");
        ColumnMap map = new(hasHeaders: false, new Dictionary<string, string>
        {
            ["LoanAccountNumber"] = "0", ["CustomerId"] = "1", ["Amount"] = "2", ["ReportDate"] = "3"
        });

        var (agg, accts) = _loaders.LoadWriteoff(path, columnMap: map);

        Assert.Contains("A1", accts);
        Assert.Equal(300, agg[0].WriteOffAmount);
    }

    [Fact]
    public void TestLoadSourceAccountsWithAHeaderlessMapResolvesByIndex()
    {
        string path = WriteFile("ifrs9.csv", "A1,2026-06-30,150.25,Stage 2\n");
        ColumnMap map = new(hasHeaders: false, new Dictionary<string, string>
        {
            ["LoanAccountNumber"] = "0", ["AmountOutstanding"] = "2"
        });

        SourceAccountsResult res = _loaders.LoadSourceAccounts(path, "LoanAccountNumber", "test", "AmountOutstanding", columnMap: map);

        Assert.Contains("A1", res.AccountNumbers);
        Assert.Equal(150.25, res.AmountsPerAccount["A1"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter DataLoadersMappingTests`
Expected: FAIL — `LoadWriteoff`/`LoadSourceAccounts` have no `columnMap` parameter (compile error).

- [ ] **Step 3: Modify the implementation**

In `src/HazardRecon.Core/Services/DataLoaders.cs`, add `using HazardRecon.Core.Models;` is already present (the file already references `HazardRecon.Core.Models` types). Add a private helper method and a config-factory, replacing the fixed `CsvConfig` field's use in the two affected methods only (`LoadDefaults` keeps using the existing `CsvConfig` field untouched):

```csharp
    private static CsvConfiguration ConfigFor(bool hasHeaders) => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord = hasHeaders,
        MissingFieldFound = null,
        HeaderValidated = null,
        BadDataFound = null
    };

    private static string? Field(CsvReader csv, bool hasHeaders, string sourceColumn) =>
        hasHeaders
            ? csv.GetField(sourceColumn)
            : (int.TryParse(sourceColumn, out int idx) ? csv.GetField(idx) : csv.GetField(sourceColumn));
```

Replace the body of `LoadWriteoff` (keep the same method signature plus the new trailing parameter):

```csharp
    public (List<WriteOffAggRecord> AggRecords, HashSet<string> AccountSet) LoadWriteoff(
        string? path, Action<string, string>? log = null, ColumnMap? columnMap = null)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            log?.Invoke("write-off file MISSING - check 2 cannot run", LogKind.Warn);
            return (new List<WriteOffAggRecord>(), new HashSet<string>());
        }

        bool hasHeaders = columnMap?.HasHeaders ?? true;
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, ConfigFor(hasHeaders));

        if (hasHeaders)
        {
            csv.Read();
            csv.ReadHeader();
        }

        string acctCol = columnMap?.Resolve("LoanAccountNumber") ?? "LoanAccountNumber";
        string custCol = columnMap?.Resolve("CustomerId") ?? "CustomerId";
        string amtCol = columnMap?.Resolve("Amount") ?? "Amount";
        string dateCol = columnMap?.Resolve("ReportDate") ?? "ReportDate";

        List<RawWriteOffRow> rawRows = new();
        while (csv.Read())
        {
            string acct = AccountUtils.NormaliseAccount(Field(csv, hasHeaders, acctCol));
            if (string.IsNullOrEmpty(acct)) continue;

            string custId = Field(csv, hasHeaders, custCol) ?? string.Empty;
            double amt = double.TryParse(Field(csv, hasHeaders, amtCol), NumberStyles.Any, CultureInfo.InvariantCulture, out double val) ? val : 0.0;
            DateTime? reportDate = DateTime.TryParse(Field(csv, hasHeaders, dateCol), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt) ? dt : null;

            rawRows.Add(new RawWriteOffRow
            {
                AccountNormalized = acct,
                CustomerId = custId,
                Amount = amt,
                ReportDate = reportDate
            });
        }

        List<WriteOffAggRecord> agg = rawRows
            .GroupBy(r => r.AccountNormalized)
            .Select(g => new WriteOffAggRecord
            {
                AccountNormalized = g.Key,
                CustomerId = g.First().CustomerId,
                WriteOffAmount = g.Sum(r => r.Amount),
                FirstWriteOffDate = g.Where(r => r.ReportDate.HasValue).Min(r => r.ReportDate),
                LastWriteOffDate = g.Where(r => r.ReportDate.HasValue).Max(r => r.ReportDate),
                WriteOffRows = g.Count()
            })
            .ToList();

        HashSet<string> acctSet = agg.Select(a => a.AccountNormalized).ToHashSet();

        DateTime? minDate = rawRows.Where(r => r.ReportDate.HasValue).Min(r => r.ReportDate);
        DateTime? maxDate = rawRows.Where(r => r.ReportDate.HasValue).Max(r => r.ReportDate);

        string dateRangeStr = (minDate.HasValue && maxDate.HasValue) ? $" ({minDate.Value:yyyy-MM-dd} to {maxDate.Value:yyyy-MM-dd})" : "";
        log?.Invoke($"write-off: {agg.Count:N0} distinct accounts from {rawRows.Count:N0} rows{dateRangeStr}", LogKind.Ok);

        return (agg, acctSet);
    }
```

Replace the body of `LoadSourceAccounts`:

```csharp
    public SourceAccountsResult LoadSourceAccounts(
        string? path, string colName, string label, string? amountCol = null,
        Action<string, string>? log = null, ColumnMap? columnMap = null)
    {
        SourceAccountsResult res = new();

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            log?.Invoke($"{label}: file MISSING", LogKind.Warn);
            return res;
        }

        bool hasHeaders = columnMap?.HasHeaders ?? true;
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, ConfigFor(hasHeaders));

        if (hasHeaders)
        {
            csv.Read();
            csv.ReadHeader();
        }

        string resolvedColName = columnMap?.Resolve(colName) ?? colName;
        string? resolvedAmountCol = amountCol == null ? null : (columnMap?.Resolve(amountCol) ?? amountCol);

        bool hasAmountCol = resolvedAmountCol != null &&
            (!hasHeaders || (csv.HeaderRecord != null && csv.HeaderRecord.Contains(resolvedAmountCol)));

        while (csv.Read())
        {
            res.TotalRows++;
            string acct = AccountUtils.NormaliseAccount(Field(csv, hasHeaders, resolvedColName));
            if (string.IsNullOrEmpty(acct)) continue;

            res.AccountNumbers.Add(acct);

            if (hasAmountCol)
            {
                double amt = double.TryParse(Field(csv, hasHeaders, resolvedAmountCol!), NumberStyles.Any, CultureInfo.InvariantCulture, out double val) ? val : 0.0;
                res.AmountsPerAccount[acct] = res.AmountsPerAccount.GetValueOrDefault(acct, 0.0) + amt;
            }
        }

        log?.Invoke($"{label}: {res.AccountNumbers.Count:N0} distinct accounts", LogKind.Ok);
        return res;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter DataLoadersMappingTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full existing test suite to confirm nothing regressed**

Run: `dotnet test`
Expected: PASS, same total count as before plus the 4 new tests (the null-`columnMap` default must reproduce every existing `DataLoaders`/`ReconciliationEngine` test unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/HazardRecon.Core/Services/DataLoaders.cs tests/HazardRecon.Tests/DataLoadersMappingTests.cs
git commit -m "feat: resolve write-off/exposure columns through an optional ColumnMap"
```

---

### Task 8: `ReconciliationEngine` — thread per-set column maps through `Run`

**Files:**
- Modify: `src/HazardRecon.Core/Services/ReconciliationEngine.cs`
- Test: `tests/HazardRecon.Tests/ReconciliationEngineMappingTests.cs`

**Interfaces:**
- Consumes: `SetColumnMaps`, `ColumnMap` (Task 2); `DataLoaders.LoadWriteoff`/`LoadSourceAccounts`'s new `columnMap` parameter (Task 7).
- Produces: `Run(object root, string outdir = "output", Action<string,string>? logger = null, bool analyze = false, AiAnalysisService? analyst = null, StageReporter? stages = null, IReadOnlyDictionary<string, SetColumnMaps>? columnMaps = null)` — same return type, one new trailing optional parameter. Task 13 (`/api/run` wiring) is the only new caller; every existing caller (CLI, all current tests) is unaffected since the parameter defaults to `null`.

- [ ] **Step 1: Write the failing test**

This reuses `SyntheticDataFixture` (already used by `DashboardPayloadTests`, `ReconciliationEngineTests` etc.) but writes the write-off file with renamed, headerless columns to prove the mapping actually reaches the engine.

```csharp
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class ReconciliationEngineMappingTests : IClassFixture<SyntheticDataFixture>
{
    private readonly SyntheticDataFixture _fixture;

    public ReconciliationEngineMappingTests(SyntheticDataFixture fixture) => _fixture = fixture;

    [Fact]
    public void TestARenamedHeaderlessWriteoffFileIsReadCorrectlyWithAMap()
    {
        // same four write-off accounts as the fixture's own file, but with the
        // header row stripped and the columns in a different order
        string renamedWriteoff = Path.Combine(_fixture.RootDir, "renamed_writeoff.csv");
        File.WriteAllText(renamedWriteoff,
            "1,2026-03-01,100,C1,A1\n" +
            "1,2026-03-01,400,C4,A4\n" +
            "1,2026-07-01,500,C5,A5\n" +
            "1,2026-06-01,600,C6,A6\n");

        ColumnMap writeoffMap = new(hasHeaders: false, new Dictionary<string, string>
        {
            ["ReportDate"] = "1", ["Amount"] = "2", ["CustomerId"] = "3", ["LoanAccountNumber"] = "4"
        });

        ReconciliationEngine engine = new();
        var results = engine.Run(
            _fixture.RootDir, Path.Combine(_fixture.OutDir, "mapping-writeoff"),
            logger: (_, _) => { }, analyze: false, analyst: null, stages: null,
            columnMaps: new Dictionary<string, SetColumnMaps>
            {
                ["JUN2026 0.5PCT"] = new SetColumnMaps(writeoffMap, null)
            }).Results;

        // the fixture's own (headered) write-off file would have produced the
        // same trace outcome, so this proves the renamed/headerless file was
        // actually read, not silently skipped
        var summary = results.Single().Value.Summary;
        Assert.True(summary.TracedWriteOff > 0);
    }
}
```

Note: this test asserts against the *existing* `SyntheticDataFixture` set key `"JUN2026 0.5PCT"` (from `SetKeyFromFolder` applied to its folder name `"3. DEBUG FILE 30 JUNE 2026 0.5 PERCENT"`) — check `ReconciliationEngineTests.cs` for the exact key already asserted there if this doesn't match; use whatever key those tests already use.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter ReconciliationEngineMappingTests`
Expected: FAIL — `Run` has no `columnMaps` parameter (compile error).

- [ ] **Step 3: Modify the implementation**

In `src/HazardRecon.Core/Services/ReconciliationEngine.cs`:

Change the `Run` signature (line 162):
```csharp
    public ReconciliationRunResult Run(
        object root, string outdir = "output", Action<string, string>? logger = null,
        bool analyze = false, AiAnalysisService? analyst = null, StageReporter? stages = null,
        IReadOnlyDictionary<string, SetColumnMaps>? columnMaps = null)
```

Change `GetWoFor` (lines 213-224) to accept and use a per-call map:
```csharp
        Dictionary<string, (List<WriteOffAggRecord> Agg, HashSet<string> Accts)> woCache = new();
        (List<WriteOffAggRecord> Agg, HashSet<string> Accts) GetWoFor(InventorySet setInfo, ColumnMap? writeOffMap)
        {
            string? path = setInfo.WriteOff ?? inv.WriteOff;
            if (path == null) return (new List<WriteOffAggRecord>(), new HashSet<string>());

            if (!woCache.TryGetValue(path, out var cached))
            {
                cached = _dataLoaders.LoadWriteoff(path, log, writeOffMap);
                woCache[path] = cached;
            }
            return cached;
        }
```

Change the per-set loop body (lines 228-239) to look up and pass this set's maps:
```csharp
        foreach (var (key, setInfo) in inv.Sets)
        {
            log($"===== {key}  ({setInfo.Label}) =====", LogKind.Head);

            SetColumnMaps? setMaps = columnMaps?.GetValueOrDefault(key);

            var (woAgg, woAccts, engine, defaults, ifrs9Res) = stages.Track(StageKeys.Load(key), () =>
            {
                var (agg, accts) = GetWoFor(setInfo, setMaps?.WriteOff);
                return (agg, accts,
                    _dataLoaders.LoadScenario(setInfo.Scenario, setInfo.DebugJson, log),
                    _dataLoaders.LoadDefaults(setInfo.LgdDefaults, log),
                    _dataLoaders.LoadSourceAccounts(setInfo.Ifrs9, "LoanAccountNumber", $"{key} IFRS9", "AmountOutstanding", log, setMaps?.Exposure));
            });
```

Everything else in `Run` is unchanged.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter ReconciliationEngineMappingTests`
Expected: PASS.

- [ ] **Step 5: Run the full existing test suite to confirm nothing regressed**

Run: `dotnet test`
Expected: PASS, same total as Task 7's step 5 plus this task's new test(s).

- [ ] **Step 6: Commit**

```bash
git add src/HazardRecon.Core/Services/ReconciliationEngine.cs tests/HazardRecon.Tests/ReconciliationEngineMappingTests.cs
git commit -m "feat: thread per-set column maps through ReconciliationEngine.Run"
```

---

### Task 9: Column mapping stores (`saved_column_mappings` / `run_set_column_mappings`)

**Files:**
- Create: `src/HazardRecon.Web/Runs/SavedColumnMappingRecord.cs`
- Create: `src/HazardRecon.Web/Runs/RunSetColumnMappingRecord.cs`
- Create: `src/HazardRecon.Web/Runs/IColumnMappingStore.cs`
- Create: `src/HazardRecon.Web/Runs/SupabaseColumnMappingStore.cs`
- Test: `tests/HazardRecon.Tests/Web/SupabaseColumnMappingStoreTests.cs`

**Interfaces:**
- Consumes: `SupabaseRestClient` (existing, `HazardRecon.Web.Supabase`).
- Produces: `IColumnMappingStore` with `GetSavedMappingAsync(Guid userId, string fileKind, string columnSignature, CancellationToken ct = default) : Task<IReadOnlyDictionary<string,string>>`, `SaveMappingAsync(Guid userId, string fileKind, string columnSignature, IReadOnlyDictionary<string,string> mapping, CancellationToken ct = default) : Task`, `RecordRunMappingAsync(Guid runId, string setKey, string fileKind, IReadOnlyDictionary<string,string> mapping, CancellationToken ct = default) : Task`. Task 11/12 (`/api/discover`, `/api/discover/mapping`) are the only consumers.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using HazardRecon.Tests.Llm;
using HazardRecon.Web.Runs;
using HazardRecon.Web.Supabase;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseColumnMappingStoreTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co", AnonKey = "anon-key", ServiceRoleKey = "service-key"
    };

    private static SupabaseColumnMappingStore Store(FakeHttpMessageHandler handler) =>
        new(new SupabaseRestClient(Options(), handler));

    [Fact]
    public async Task TestGetSavedMappingReturnsFieldToColumnDictionary()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK,
            """[{"field_name":"LoanAccountNumber","source_column":"Column 1"},{"field_name":"AmountOutstanding","source_column":"Column 3"}]"""));

        IReadOnlyDictionary<string, string> mapping =
            await Store(handler).GetSavedMappingAsync(UserId, "exposure", "abc123");

        Assert.Equal("Column 1", mapping["LoanAccountNumber"]);
        Assert.Equal("Column 3", mapping["AmountOutstanding"]);
        Assert.Contains($"user_id=eq.{UserId}", handler.Requests[0].Url);
        Assert.Contains("file_kind=eq.exposure", handler.Requests[0].Url);
        Assert.Contains("column_signature=eq.abc123", handler.Requests[0].Url);
    }

    [Fact]
    public async Task TestGetSavedMappingReturnsEmptyWhenNoneSaved()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        IReadOnlyDictionary<string, string> mapping =
            await Store(handler).GetSavedMappingAsync(UserId, "writeoff", "xyz");

        Assert.Empty(mapping);
    }

    [Fact]
    public async Task TestSaveMappingUpsertsWithMergeDuplicates()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        await Store(handler).SaveMappingAsync(UserId, "writeoff", "sig1",
            new Dictionary<string, string> { ["Amount"] = "Amount" });

        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Contains("on_conflict=user_id,file_kind,column_signature,field_name", handler.Requests[0].Url);
        Assert.Contains("\"source_column\":\"Amount\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TestSaveMappingWithNoEntriesSendsNothing()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        await Store(handler).SaveMappingAsync(UserId, "writeoff", "sig1", new Dictionary<string, string>());

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TestRecordRunMappingDeletesThenInsertsForTheSetAndFileKind()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));
        Guid runId = Guid.NewGuid();

        await Store(handler).RecordRunMappingAsync(runId, "JUN2026", "exposure",
            new Dictionary<string, string> { ["LoanAccountNumber"] = "Column 1" });

        Assert.Equal("DELETE", handler.Requests[0].Method);
        Assert.Contains($"run_id=eq.{runId}", handler.Requests[0].Url);
        Assert.Contains("set_key=eq.JUN2026", handler.Requests[0].Url);
        Assert.Contains("file_kind=eq.exposure", handler.Requests[0].Url);
        Assert.Equal("POST", handler.Requests[1].Method);
        Assert.Contains("\"source_column\":\"Column 1\"", handler.Requests[1].Body);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter SupabaseColumnMappingStoreTests`
Expected: FAIL — none of the new types exist.

- [ ] **Step 3: Write the implementation**

`src/HazardRecon.Web/Runs/SavedColumnMappingRecord.cs`:
```csharp
using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>One row of public.saved_column_mappings - a reusable mapping for one field of one file kind.</summary>
public class SavedColumnMappingRecord
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("file_kind")]
    public string FileKind { get; set; } = string.Empty;

    [JsonPropertyName("column_signature")]
    public string ColumnSignature { get; set; } = string.Empty;

    [JsonPropertyName("field_name")]
    public string FieldName { get; set; } = string.Empty;

    [JsonPropertyName("source_column")]
    public string SourceColumn { get; set; } = string.Empty;
}
```

`src/HazardRecon.Web/Runs/RunSetColumnMappingRecord.cs`:
```csharp
using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>One row of public.run_set_column_mappings - the mapping actually used for one run's set.</summary>
public class RunSetColumnMappingRecord
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("run_id")]
    public Guid RunId { get; set; }

    [JsonPropertyName("set_key")]
    public string SetKey { get; set; } = string.Empty;

    [JsonPropertyName("file_kind")]
    public string FileKind { get; set; } = string.Empty;

    [JsonPropertyName("field_name")]
    public string FieldName { get; set; } = string.Empty;

    [JsonPropertyName("source_column")]
    public string SourceColumn { get; set; } = string.Empty;
}
```

`src/HazardRecon.Web/Runs/IColumnMappingStore.cs`:
```csharp
namespace HazardRecon.Web.Runs;

/// <summary>Persistence for column mappings: the reusable saved profile, and what a run actually used.</summary>
public interface IColumnMappingStore
{
    /// <summary>The saved field-to-column mapping for this user/file kind/column shape, if one was ever confirmed.</summary>
    Task<IReadOnlyDictionary<string, string>> GetSavedMappingAsync(
        Guid userId, string fileKind, string columnSignature, CancellationToken ct = default);

    /// <summary>Upserts the saved mapping so a future upload of the same column shape reuses it.</summary>
    Task SaveMappingAsync(
        Guid userId, string fileKind, string columnSignature,
        IReadOnlyDictionary<string, string> mapping, CancellationToken ct = default);

    /// <summary>Replaces the audit record of what this run's set actually used for this file kind.</summary>
    Task RecordRunMappingAsync(
        Guid runId, string setKey, string fileKind,
        IReadOnlyDictionary<string, string> mapping, CancellationToken ct = default);
}
```

`src/HazardRecon.Web/Runs/SupabaseColumnMappingStore.cs`:
```csharp
using System.Text;
using System.Text.Json;
using HazardRecon.Web.Supabase;

namespace HazardRecon.Web.Runs;

public class SupabaseColumnMappingStore : IColumnMappingStore
{
    private const string SavedTable = "/rest/v1/saved_column_mappings";
    private const string RunTable = "/rest/v1/run_set_column_mappings";

    private static readonly Dictionary<string, string> MergeDuplicates =
        new() { ["Prefer"] = "resolution=merge-duplicates" };

    private readonly SupabaseRestClient _rest;

    public SupabaseColumnMappingStore(SupabaseRestClient rest) => _rest = rest;

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public async Task<IReadOnlyDictionary<string, string>> GetSavedMappingAsync(
        Guid userId, string fileKind, string columnSignature, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{SavedTable}?user_id=eq.{userId}&file_kind=eq.{fileKind}&column_signature=eq.{columnSignature}&select=field_name,source_column",
            null, null, ct);

        List<SavedColumnMappingRecord> rows =
            JsonSerializer.Deserialize<List<SavedColumnMappingRecord>>(body) ?? new List<SavedColumnMappingRecord>();

        return rows.ToDictionary(r => r.FieldName, r => r.SourceColumn);
    }

    public async Task SaveMappingAsync(
        Guid userId, string fileKind, string columnSignature,
        IReadOnlyDictionary<string, string> mapping, CancellationToken ct = default)
    {
        if (mapping.Count == 0) return;

        var rows = mapping.Select(kv => new
        {
            user_id = userId,
            file_kind = fileKind,
            column_signature = columnSignature,
            field_name = kv.Key,
            source_column = kv.Value,
            last_used_at = DateTimeOffset.UtcNow
        });

        await _rest.SendAsync(HttpMethod.Post,
            $"{SavedTable}?on_conflict=user_id,file_kind,column_signature,field_name",
            Json(rows), MergeDuplicates, ct);
    }

    public async Task RecordRunMappingAsync(
        Guid runId, string setKey, string fileKind,
        IReadOnlyDictionary<string, string> mapping, CancellationToken ct = default)
    {
        string encodedSetKey = Uri.EscapeDataString(setKey);
        await _rest.SendAsync(HttpMethod.Delete,
            $"{RunTable}?run_id=eq.{runId}&set_key=eq.{encodedSetKey}&file_kind=eq.{fileKind}", null, null, ct);

        if (mapping.Count == 0) return;

        var rows = mapping.Select(kv => new
        {
            run_id = runId,
            set_key = setKey,
            file_kind = fileKind,
            field_name = kv.Key,
            source_column = kv.Value
        });

        await _rest.SendAsync(HttpMethod.Post, RunTable, Json(rows), null, ct);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter SupabaseColumnMappingStoreTests`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Web/Runs/SavedColumnMappingRecord.cs src/HazardRecon.Web/Runs/RunSetColumnMappingRecord.cs \
        src/HazardRecon.Web/Runs/IColumnMappingStore.cs src/HazardRecon.Web/Runs/SupabaseColumnMappingStore.cs \
        tests/HazardRecon.Tests/Web/SupabaseColumnMappingStoreTests.cs
git commit -m "feat: add SupabaseColumnMappingStore for saved and per-run mappings"
```

---

### Task 10: `SetFileReceiver` — replace the folder upload with per-kind files

**Files:**
- Create: `src/HazardRecon.Web/Uploads/SetFileReceiver.cs`
- Delete: `src/HazardRecon.Web/Uploads/UploadReceiver.cs`, `src/HazardRecon.Web/Uploads/UploadPath.cs`
- Delete: `tests/HazardRecon.Tests/Web/UploadReceiverTests.cs`, `tests/HazardRecon.Tests/Web/UploadPathTests.cs`
- Test: `tests/HazardRecon.Tests/Web/SetFileReceiverTests.cs`

**Interfaces:**
- Produces: `SetFileKind` enum (`Exposure`, `Writeoff`, `Debug`, `Scenario`); `SetFileItem(int SetIndex, SetFileKind Kind, string OriginalFileName, Stream Content, long Length)`; `ReceivedSet(string Root, string Label, string ExposureFileName, string WriteOffFileName, int FileCount, long Bytes)`; `SetReceiveOutcome(bool Ok, string? Error, IReadOnlyList<ReceivedSet> Sets)` with `SetReceiveOutcome.Fail(string)`; `SetFileReceiver(long maxBytesPerSet = SetFileReceiver.DefaultMaxBytesPerSet)` with `.ReceiveAsync(string destinationRoot, IReadOnlyList<SetFileItem> items, CancellationToken ct = default) : Task<SetReceiveOutcome>`; constants `SetFileReceiver.MaxSets`, `SetFileReceiver.DefaultMaxBytesPerSet`. Task 12 (`/api/discover`) is the only consumer. Each set's folder, once written, is exactly what `InputDiscoverer.BuildSet` already expects (`IFRS9.csv`, `writeoff.csv`, `scenario.json`, plus the debug file(s) under their real names) — no changes to `InputDiscoverer` are needed (see "Deviations from the spec" above).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;
using HazardRecon.Web.Uploads;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SetFileReceiverTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "hr-setfile-tests", Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static SetFileItem Item(int set, SetFileKind kind, string originalName, string content = "x") =>
        new(set, kind, originalName, new MemoryStream(Encoding.UTF8.GetBytes(content)), content.Length);

    private static SetFileItem Sized(int set, SetFileKind kind, string originalName, long length) =>
        new(set, kind, originalName, new MemoryStream(), length);

    private static IReadOnlyList<SetFileItem> FullSet(int index = 0) => new[]
    {
        Item(index, SetFileKind.Exposure, "IFRS9 FILE JUNE 2026.csv", "a,b\n1,2\n"),
        Item(index, SetFileKind.Writeoff, "2026_WRITEOFF.csv", "c,d\n3,4\n"),
        Item(index, SetFileKind.Debug, "debug.zip", "zipbytes"),
        Item(index, SetFileKind.Scenario, "scenario.json", "{}"),
    };

    [Fact]
    public async Task TestEachFileLandsUnderItsCanonicalName()
    {
        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, FullSet());

        Assert.True(result.Ok, result.Error);
        ReceivedSet set = Assert.Single(result.Sets);
        Assert.True(File.Exists(Path.Combine(set.Root, "IFRS9.csv")));
        Assert.True(File.Exists(Path.Combine(set.Root, "writeoff.csv")));
        Assert.True(File.Exists(Path.Combine(set.Root, "debug.zip")));
        Assert.True(File.Exists(Path.Combine(set.Root, "scenario.json")));
        Assert.Equal("a,b\n1,2\n", File.ReadAllText(Path.Combine(set.Root, "IFRS9.csv")));
    }

    [Fact]
    public async Task TestTheLabelDefaultsToTheExposureFileNameWithoutExtension()
    {
        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, FullSet());

        Assert.Equal("IFRS9 FILE JUNE 2026", result.Sets[0].Label);
        Assert.Equal("IFRS9 FILE JUNE 2026.csv", result.Sets[0].ExposureFileName);
        Assert.Equal("2026_WRITEOFF.csv", result.Sets[0].WriteOffFileName);
    }

    [Fact]
    public async Task TestALooseDebugFileSetKeepsItsOwnNames()
    {
        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, new[]
        {
            Item(0, SetFileKind.Exposure, "ifrs9.csv"),
            Item(0, SetFileKind.Writeoff, "wo.csv"),
            Item(0, SetFileKind.Debug, "lgd_defaults.csv"),
            Item(0, SetFileKind.Debug, "pd_scored.csv"),
            Item(0, SetFileKind.Debug, "debug.json"),
            Item(0, SetFileKind.Scenario, "scenario.json"),
        });

        Assert.True(result.Ok, result.Error);
        Assert.True(File.Exists(Path.Combine(result.Sets[0].Root, "lgd_defaults.csv")));
        Assert.True(File.Exists(Path.Combine(result.Sets[0].Root, "pd_scored.csv")));
        Assert.True(File.Exists(Path.Combine(result.Sets[0].Root, "debug.json")));
    }

    [Fact]
    public async Task TestTwoSetsDoNotCollide()
    {
        List<SetFileItem> items = FullSet(0).Concat(FullSet(1)).ToList();

        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, items);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(2, result.Sets.Count);
        Assert.NotEqual(result.Sets[0].Root, result.Sets[1].Root);
    }

    [Fact]
    public async Task TestAMissingExposureFileIsRejected()
    {
        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, new[]
        {
            Item(0, SetFileKind.Writeoff, "wo.csv"),
            Item(0, SetFileKind.Debug, "debug.zip"),
            Item(0, SetFileKind.Scenario, "scenario.json"),
        });

        Assert.False(result.Ok);
        Assert.Contains("exposure", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestNoFilesIsRefused()
    {
        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, Array.Empty<SetFileItem>());

        Assert.False(result.Ok);
        Assert.Contains("at least one", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestMoreThanFourSetsIsRefused()
    {
        List<SetFileItem> items = Enumerable.Range(0, 5).SelectMany(i => FullSet(i)).ToList();

        SetReceiveOutcome result = await new SetFileReceiver().ReceiveAsync(_root, items);

        Assert.False(result.Ok);
        Assert.Contains("maximum of 4", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAnOversizedSetIsRefusedBeforeAnythingIsWritten()
    {
        long limit = 10L * 1024 * 1024;

        SetReceiveOutcome result = await new SetFileReceiver(limit).ReceiveAsync(_root, new[]
        {
            Sized(0, SetFileKind.Exposure, "IFRS9.csv", limit / 2),
            Sized(0, SetFileKind.Writeoff, "wo.csv", limit),
        });

        Assert.False(result.Ok);
        Assert.Contains("limit is 10 MB", result.Error!);
        Assert.False(Directory.Exists(Path.Combine(_root, "0")));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter SetFileReceiverTests`
Expected: FAIL — `SetFileReceiver` does not exist.

- [ ] **Step 3: Write the implementation**

```csharp
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
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter SetFileReceiverTests`
Expected: PASS (8 tests).

- [ ] **Step 5: Delete the replaced files**

```bash
git rm src/HazardRecon.Web/Uploads/UploadReceiver.cs src/HazardRecon.Web/Uploads/UploadPath.cs \
       tests/HazardRecon.Tests/Web/UploadReceiverTests.cs tests/HazardRecon.Tests/Web/UploadPathTests.cs
```

Do not build yet — `Program.cs` still references `UploadReceiver`/`UploadItem`/`UploadOutcome`; Task 12 fixes that. Leaving the build red between this step and Task 12 within the same PR/branch is fine since this plan's tasks land as one sequence, but if executing tasks with review gates between them, note this task alone will not compile standalone — call this out to the reviewer rather than treating a red build as a defect at this checkpoint.

- [ ] **Step 6: Commit**

```bash
git add src/HazardRecon.Web/Uploads/SetFileReceiver.cs tests/HazardRecon.Tests/Web/SetFileReceiverTests.cs
git commit -m "feat: replace the folder upload receiver with per-file-kind uploads"
```

---

### Task 11: `JobState` — stash mappable-file info and confirmed column maps

**Files:**
- Modify: `src/HazardRecon.Web/JobState.cs`

**Interfaces:**
- Consumes: `ColumnMap`, `SetColumnMaps` (Task 2).
- Produces: `JobState.MappableFiles : Dictionary<string, MappableSetFiles>` and `JobState.ColumnMaps : Dictionary<string, SetColumnMaps>`; `MappableSetFiles(string WriteOffPath, bool WriteOffHasHeaders, string ExposurePath, bool ExposureHasHeaders)`. Task 12 (`/api/discover`) populates `MappableFiles`; Task 13 (`/api/discover/mapping`) reads `MappableFiles` and populates `ColumnMaps`; Task 14 (`/api/run` wiring) reads `ColumnMaps`.

There is no dedicated test file for `JobState` itself (it is a plain internal data holder exercised through the endpoint tests in Tasks 12-14) — this task's own verification is the build.

- [ ] **Step 1: Modify `JobState.cs`**

Add the new record and two dictionary properties, alongside the existing `JobLogEntry` record and `JobState` class:

```csharp
using HazardRecon.Core.Models;

namespace HazardRecon.Web;

/// <summary>One line the engine logged, with the real timestamp public.logs needs.</summary>
internal record JobLogEntry(DateTimeOffset OccurredAt, string Message, string Kind);

/// <summary>
/// Where a set's mappable files are on disk and whether each has a header row -
/// populated once discovery runs, consumed once the mapping is confirmed.
/// </summary>
internal record MappableSetFiles(string WriteOffPath, bool WriteOffHasHeaders, string ExposurePath, bool ExposureHasHeaders);

internal class JobState
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Owner of the run, taken from the token that created it.</summary>
    public Guid UserId { get; set; }

    public string Status { get; set; } = "ready";
    public List<string> Roots { get; set; } = new();
    public string Outdir { get; set; } = string.Empty;
    public string Indir { get; set; } = string.Empty;
    public List<JobLogEntry> Log { get; set; } = new();
    public object? Result { get; set; }
    public string? Error { get; set; }
    public string Started { get; set; } = string.Empty;

    /// <summary>How far the run has got. Replaced wholesale on every engine update.</summary>
    public IReadOnlyList<RunStage> Stages { get; set; } = Array.Empty<RunStage>();

    /// <summary>When the current attempt began, for the elapsed clock on the progress screen.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Set when the attempt ends, so the elapsed clock stops instead of climbing forever.</summary>
    public DateTimeOffset? FinishedAt { get; set; }

    public string? ModelId { get; set; }
    public Dictionary<string, object>? AnalysisPayload { get; set; }

    /// <summary>Keyed by set key; populated by /api/discover, consumed by /api/discover/mapping.</summary>
    public Dictionary<string, MappableSetFiles> MappableFiles { get; set; } = new();

    /// <summary>Keyed by set key; populated by /api/discover/mapping, consumed by /api/run.</summary>
    public Dictionary<string, SetColumnMaps> ColumnMaps { get; set; } = new();
}
```

- [ ] **Step 2: Confirm it builds**

Run: `dotnet build`
Expected: builds clean (this task adds fields nothing references yet — no behavior to test in isolation).

- [ ] **Step 3: Commit**

```bash
git add src/HazardRecon.Web/JobState.cs
git commit -m "feat: stash mappable-file info and confirmed column maps on JobState"
```

---

### Task 12: `POST /api/discover` redesign

**Files:**
- Modify: `src/HazardRecon.Web/Program.cs`
- Modify: `src/HazardRecon.Core/Llm/CyteLlmOptions.cs`
- Modify: `tests/HazardRecon.Tests/Web/UploadEndpointTests.cs` (rewritten)

**Interfaces:**
- Consumes: `SetFileReceiver`, `SetFileItem`, `SetFileKind` (Task 10); `CsvSniffer` (Task 4); `ColumnSignature` (Task 5); `ColumnMappingService`, `ResolvedField`, `MappableFields` (Task 6, Task 3); `IColumnMappingStore` (Task 9); `JobState.MappableFiles`, `MappableSetFiles` (Task 11).
- Produces: the redesigned discover response shape (asserted by this task's tests) that Task 13's frontend (out of scope here) and this plan's own Task 13 (`/api/discover/mapping`, which reads the same `job.MappableFiles`) depend on.

- [ ] **Step 1: Add `MappingModelId` to `CyteLlmOptions`**

```csharp
namespace HazardRecon.Core.Llm;

public class CyteLlmOptions
{
    public string TokenUrl { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Fixed model used for AI-assisted column-mapping guesses, independent of whatever
    /// model the user later picks for the run's AI analysis. Column mapping is skipped
    /// (falls back to manual) when this is unset, same as AI analysis when the gateway itself is unconfigured.</summary>
    public string? MappingModelId { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TokenUrl) &&
        !string.IsNullOrWhiteSpace(Audience) &&
        !string.IsNullOrWhiteSpace(ApiBaseUrl) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
```

- [ ] **Step 2: Rewrite the failing/changed integration tests**

Replace the whole content of `tests/HazardRecon.Tests/Web/UploadEndpointTests.cs`. The `AuthedFactory`/`AlwaysOnHandler` scaffolding is unchanged; only the multipart shape and assertions change (folder relative paths → tagged files):

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HazardRecon.Web.Files;
using HazardRecon.Web.Runs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>
/// Drives POST /api/discover with a real multipart body tagging each file by
/// set index and role (set{N}.{kind}), the contract SetFileReceiver expects.
/// </summary>
public class UploadEndpointTests : IClassFixture<UploadEndpointTests.AuthedFactory>
{
    private class AlwaysOnHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public AlwaysOnHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Claim[] claims = { new("sub", "11111111-1111-1111-1111-111111111111") };
            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, "Test"));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, "Test")));
        }
    }

    public class AuthedFactory : WebApplicationFactory<Program>
    {
        public FakeRunStore RunStore { get; } = new();
        public FakeColumnMappingStore MappingStore { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Supabase:Url"] = "https://ref.supabase.co",
                    ["Supabase:AnonKey"] = "anon-key-for-tests",
                    ["Supabase:ServiceRoleKey"] = "service-key-for-tests"
                }));

            builder.ConfigureServices(services =>
            {
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, AlwaysOnHandler>("Test", _ => { });
                services.PostConfigure<AuthenticationOptions>(o =>
                {
                    o.DefaultAuthenticateScheme = "Test";
                    o.DefaultChallengeScheme = "Test";
                    o.DefaultScheme = "Test";
                });

                services.RemoveAll<IRunStore>();
                services.RemoveAll<IRunFileStore>();
                services.RemoveAll<IFileStore>();
                services.RemoveAll<IColumnMappingStore>();
                services.AddSingleton<IRunStore>(RunStore);
                services.AddSingleton<IRunFileStore>(new FakeRunFileStore());
                services.AddSingleton<IFileStore>(new FakeFileStore());
                services.AddSingleton<IColumnMappingStore>(MappingStore);
            });

            return base.CreateHost(builder);
        }
    }

    private readonly AuthedFactory _factory;

    public UploadEndpointTests(AuthedFactory factory) => _factory = factory;

    private static void AddFile(MultipartFormDataContent form, int setIndex, string kind, string fileName, string body)
    {
        ByteArrayContent content = new(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, $"set{setIndex}.{kind}", fileName);
    }

    private static void AddFullSet(MultipartFormDataContent form, int setIndex, string exposureName = "IFRS9 FILE.csv")
    {
        AddFile(form, setIndex, "exposure", exposureName, "A1,2026-06-30,100,Stage 2\n");
        AddFile(form, setIndex, "writeoff", "WRITEOFF.csv", "LoanAccountNumber,CustomerId,Amount,ReportDate\nA1,C1,100,2026-04-30\n");
        AddFile(form, setIndex, "debug", "debug.zip", "zipbytes");
        AddFile(form, setIndex, "scenario", "scenario.json", "{}");
    }

    [Fact]
    public async Task TestAnUploadedSetIsRecordedAgainstTheCaller()
    {
        HttpClient client = _factory.CreateClient();
        int before = _factory.RunStore.Runs.Count;

        using MultipartFormDataContent form = new();
        AddFullSet(form, 0, "MAR 2026.csv");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("run_id", body);
        Assert.Equal(before + 1, _factory.RunStore.Runs.Count);

        RunRecord run = _factory.RunStore.Runs[^1];
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), run.UserId);
        Assert.Contains("MAR 2026", run.SetLabels);
    }

    [Fact]
    public async Task TestTheResponseIncludesMappingDataForBothCsvFiles()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFullSet(form, 0);

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"writeoff\"", body);
        Assert.Contains("\"exposure\"", body);
        // the write-off file's real headers were matched by name, no AI guess needed
        Assert.Contains("header_match", body);
    }

    [Fact]
    public async Task TestTwoSetsBecomeTwoInventoryEntries()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFullSet(form, 0, "JAN 2026.csv");
        AddFullSet(form, 1, "FEB 2026.csv");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("JAN 2026", body);
        Assert.Contains("FEB 2026", body);
    }

    [Fact]
    public async Task TestAMissingRequiredFileIsRejected()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFile(form, 0, "writeoff", "WRITEOFF.csv", "a,b\n1,2\n");
        AddFile(form, 0, "debug", "debug.zip", "zipbytes");
        AddFile(form, 0, "scenario", "scenario.json", "{}");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("exposure", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAnUnknownFieldNameIsRejected()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFile(form, 0, "notakind", "x.csv", "data");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TestTheRunIdIsTheDatabaseId()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();
        AddFullSet(form, 0, "APR 2026.csv");

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains(_factory.RunStore.Runs[^1].Id.ToString(), body);
    }

    [Fact]
    public async Task TestTheDailyRunQuotaIsEnforced()
    {
        HttpClient client = _factory.CreateClient();
        _factory.RunStore.RecentCount = 20;

        try
        {
            using MultipartFormDataContent form = new();
            AddFullSet(form, 0, "MAY 2026.csv");

            HttpResponseMessage response = await client.PostAsync("/api/discover", form);
            string body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Contains("limit is 20", body);
        }
        finally
        {
            _factory.RunStore.RecentCount = 0;
        }
    }

    [Fact]
    public async Task TestAnEmptyUploadIsRejected()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent form = new();

        HttpResponseMessage response = await client.PostAsync("/api/discover", form);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at least one", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TestAnotherUsersRunIsReportedMissingNotForbidden()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync($"/api/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TestHistoryListsOnlyTheCallersRuns()
    {
        HttpClient client = _factory.CreateClient();

        await _factory.RunStore.CreateAsync(Guid.NewGuid(), new[] { "SOMEONE ELSE" });

        HttpResponseMessage response = await client.GetAsync("/api/runs");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("SOMEONE ELSE", body);
    }
}
```

Add a `FakeColumnMappingStore` to `tests/HazardRecon.Tests/Web/Fakes.cs` (in-memory, no saved mappings by default so every test exercises the "no saved mapping yet" path):

```csharp
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
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter UploadEndpointTests`
Expected: FAIL (compile errors — `Program.cs` still expects the old multipart shape and types).

- [ ] **Step 4: Rewrite the `/api/discover` handler**

In `src/HazardRecon.Web/Program.cs`:

Register the new store and mapping service (near the other `AddSingleton` calls, after `IChatStore`):
```csharp
builder.Services.AddSingleton<IColumnMappingStore>(sp => new SupabaseColumnMappingStore(sp.GetRequiredService<SupabaseRestClient>()));
```

Update the two `UploadReceiver.*` references used for Kestrel/form limits (currently `UploadReceiver.DefaultMaxBytesPerSet`, `UploadReceiver.MaxSets`, `UploadReceiver.MaxFilesPerSet`) to `SetFileReceiver`'s equivalents — `SetFileReceiver` has no per-set file-count cap (it always expects exactly 4 files, or up to 6 if debug is 3 loose files), so replace:
```csharp
long maxBytesPerSet = builder.Configuration.GetValue<long?>("Uploads:MaxBytesPerSet")
    ?? SetFileReceiver.DefaultMaxBytesPerSet;
long maxRequestBytes = maxBytesPerSet * SetFileReceiver.MaxSets + (16L * 1024 * 1024);

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxRequestBytes);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = maxRequestBytes;
    o.MultipartHeadersLengthLimit = 65536;
    o.ValueCountLimit = 8 * SetFileReceiver.MaxSets + 32; // 4 kinds, up to 3 loose debug files
});
```

Construct the mapping service alongside the existing `llm` setup (after the existing `if (llm == null) { Console.WriteLine(...); }` block):
```csharp
ColumnMappingService? columnMapper = (llm != null && !string.IsNullOrWhiteSpace(llmOptions.MappingModelId))
    ? new ColumnMappingService(llm, llmOptions.MappingModelId!)
    : null;
```

Resolve the store after `app.Build()`, alongside the other resolved services:
```csharp
IColumnMappingStore columnMappingStore = app.Services.GetRequiredService<IColumnMappingStore>();
```

Replace the entire `/api/discover` endpoint (from `// POST /api/discover ...` through its closing `}).RequireAuthorization();`) with:

```csharp
// POST /api/discover - receives one exposure (IFRS9), one write-off, one
// debug (zip or its 1-3 loose extracted files), and one scenario file per
// set, each tagged by the client as set{N}.{kind}. Rehydrates each under the
// canonical name InputDiscoverer.BuildSet already looks for, so file *role*
// is never guessed - only the write-off/exposure CSVs' *columns* need a
// mapping, resolved here and returned for the Map-columns step to confirm.
app.MapPost("/api/discover", async (HttpContext ctx) =>
{
    if (!ctx.Request.HasFormContentType)
        return Results.BadRequest(new { error = "Please choose your files." });

    IFormCollection form;
    try
    {
        form = await ctx.Request.ReadFormAsync();
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = "Please choose your files.", detail = ex.GetType().Name });
    }

    List<SetFileItem> items = new();
    foreach (IFormFile file in form.Files)
    {
        string[] parts = file.Name.Split('.', 2);
        if (parts.Length != 2 || !parts[0].StartsWith("set", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(parts[0].AsSpan(3), out int setIndex)
            || !Enum.TryParse(parts[1], ignoreCase: true, out SetFileKind kind))
        {
            return Results.BadRequest(new { error = $"Unexpected upload field '{file.Name}'." });
        }

        items.Add(new SetFileItem(setIndex, kind, file.FileName, file.OpenReadStream(), file.Length));
    }

    Guid? userId = SupabaseJwt.UserId(ctx.User);
    if (userId == null) return Results.Unauthorized();

    int recent = await runStore.CountSinceAsync(userId.Value, DateTimeOffset.UtcNow.AddDays(-1));
    if (recent >= RunsPerDay)
    {
        return Results.Json(
            new { error = $"You have started {recent} runs in the last 24 hours; the limit is {RunsPerDay}." },
            statusCode: 429);
    }

    if (items.Count == 0)
        return Results.BadRequest(new { error = "Please choose at least one set's files." });

    RunRecord created = await runStore.CreateAsync(
        userId.Value,
        items.Where(i => i.Kind == SetFileKind.Exposure)
            .OrderBy(i => i.SetIndex)
            .Select(i => Path.GetFileNameWithoutExtension(i.OriginalFileName))
            .ToList());

    string rid = created.Id.ToString();
    string runRoot = Path.Combine(runsDir, rid);
    string outdir = Path.Combine(runRoot, "output");
    string indir = Path.Combine(runRoot, "input");
    Directory.CreateDirectory(outdir);
    Directory.CreateDirectory(indir);

    SetReceiveOutcome received = await new SetFileReceiver(maxBytesPerSet).ReceiveAsync(indir, items, ctx.RequestAborted);
    if (!received.Ok)
    {
        Directory.Delete(runRoot, recursive: true);
        await runStore.UpdateStatusAsync(created.Id, "error", received.Error);
        return Results.BadRequest(new { error = received.Error });
    }

    jobs[rid] = new JobState
    {
        Id = rid,
        UserId = userId.Value,
        Status = "ready",
        Roots = received.Sets.Select(s => s.Root).ToList(),
        Outdir = outdir,
        Indir = indir,
        Started = DateTime.Now.ToString("o")
    };

    _ = Task.Run(async () =>
    {
        try
        {
            await persister.PersistDirectoryAsync(userId.Value, created.Id, "input", indir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($" ! could not store inputs for {rid}: {ex.Message}");
        }
    });

    var discoverer = new InputDiscoverer();
    var setViews = new List<object>();
    var mappingViews = new List<object>();
    var problems = new List<string>();

    foreach (ReceivedSet rs in received.Sets)
    {
        InventorySet? s = discoverer.BuildSet(rs.Root);
        if (s == null)
        {
            problems.Add($"{rs.Label}: no analysis data found - check the debug file.");
            continue;
        }

        s.Label = rs.Label;
        string key = InputDiscoverer.SetKeyFromFolder(rs.Label);

        setViews.Add(new
        {
            key,
            label = s.Label,
            lgd_defaults = s.LgdDefaults == null ? null : Path.GetFileName(s.LgdDefaults),
            pd_scored = s.PdScored == null ? null : Path.GetFileName(s.PdScored),
            ifrs9 = s.Ifrs9 == null ? null : Path.GetFileName(s.Ifrs9),
            scenario = s.Scenario == null ? null : Path.GetFileName(s.Scenario),
            debug_json = s.DebugJson == null ? null : Path.GetFileName(s.DebugJson),
            writeoff = s.WriteOff == null ? null : Path.GetFileName(s.WriteOff)
        });

        if (string.IsNullOrEmpty(s.WriteOff)) problems.Add($"{key}: no write-off CSV - check 2 cannot run for this set.");
        if (string.IsNullOrEmpty(s.PdScored)) problems.Add($"{key}: pd_scored.csv missing - no migrations.");
        if (string.IsNullOrEmpty(s.Scenario)) problems.Add($"{key}: scenario.json missing - no engine results.");
        if (string.IsNullOrEmpty(s.Ifrs9)) problems.Add($"{key}: no IFRS9 file - defaults can only trace to write-off.");

        string writeOffPath = Path.Combine(rs.Root, "writeoff.csv");
        string exposurePath = Path.Combine(rs.Root, "IFRS9.csv");

        CsvSniff writeoffSniff = CsvSniffer.Sniff(writeOffPath);
        CsvSniff exposureSniff = CsvSniffer.Sniff(exposurePath);

        jobs[rid].MappableFiles[key] = new MappableSetFiles(
            writeOffPath, writeoffSniff.HasHeaders, exposurePath, exposureSniff.HasHeaders);

        string writeoffSignature = ColumnSignature.Compute(writeoffSniff.Headers, writeoffSniff.SampleRows);
        string exposureSignature = ColumnSignature.Compute(exposureSniff.Headers, exposureSniff.SampleRows);

        IReadOnlyDictionary<string, string> savedWriteoff =
            await columnMappingStore.GetSavedMappingAsync(userId.Value, "writeoff", writeoffSignature);
        IReadOnlyDictionary<string, string> savedExposure =
            await columnMappingStore.GetSavedMappingAsync(userId.Value, "exposure", exposureSignature);

        IReadOnlyList<ResolvedField> writeoffFields = columnMapper != null
            ? columnMapper.Resolve(writeoffSniff.Headers, writeoffSniff.SampleRows, MappableFields.Writeoff, savedWriteoff)
            : MappableFields.Writeoff.Select(f => savedWriteoff.TryGetValue(f.Field, out string? c)
                ? new ResolvedField(f.Field, c, null, "saved")
                : new ResolvedField(f.Field, null, null, "unmapped")).ToList();

        IReadOnlyList<ResolvedField> exposureFields = columnMapper != null
            ? columnMapper.Resolve(exposureSniff.Headers, exposureSniff.SampleRows, MappableFields.Exposure, savedExposure)
            : MappableFields.Exposure.Select(f => savedExposure.TryGetValue(f.Field, out string? c)
                ? new ResolvedField(f.Field, c, null, "saved")
                : new ResolvedField(f.Field, null, null, "unmapped")).ToList();

        object FileView(CsvSniff sniff, IReadOnlyList<MappingFieldSpec> specs, IReadOnlyList<ResolvedField> resolved) => new
        {
            has_headers = sniff.HasHeaders,
            headers = sniff.Headers,
            samples = sniff.SampleRows,
            fields = specs.Select(spec =>
            {
                ResolvedField r = resolved.First(x => x.Field == spec.Field);
                return new { field = spec.Field, note = spec.Note, column = r.Column, confidence = r.Confidence, source = r.Source };
            })
        };

        mappingViews.Add(new
        {
            key,
            writeoff = FileView(writeoffSniff, MappableFields.Writeoff, writeoffFields),
            exposure = FileView(exposureSniff, MappableFields.Exposure, exposureFields)
        });
    }

    if (setViews.Count == 0)
        problems.Insert(0, "No analysis sets found. Each set needs debug.zip (or an extracted lgd_defaults.csv).");

    return Results.Ok(new
    {
        run_id = rid,
        inventory = new { root = string.Join("; ", received.Sets.Select(s => s.Root)), sets = setViews },
        problems,
        mapping = mappingViews
    });
}).RequireAuthorization();
```

Add the necessary `using` directives at the top of `Program.cs` if not already present: `using HazardRecon.Core.Services;` (already present) covers `CsvSniffer`, `ColumnSignature`, `ColumnMappingService`, `ResolvedField`; `HazardRecon.Core.Models` (already present) covers `MappableFields`, `MappingFieldSpec`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter UploadEndpointTests`
Expected: PASS (9 tests).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test`
Expected: PASS. `SetFileReceiverTests`, `UploadEndpointTests`, and everything from Tasks 1-11 all still pass; `UploadReceiverTests`/`UploadPathTests` are gone (deleted in Task 10), not failing.

- [ ] **Step 7: Commit**

```bash
git add src/HazardRecon.Web/Program.cs src/HazardRecon.Core/Llm/CyteLlmOptions.cs \
        tests/HazardRecon.Tests/Web/UploadEndpointTests.cs tests/HazardRecon.Tests/Web/Fakes.cs
git commit -m "feat: redesign POST /api/discover around per-set tagged files and column mapping"
```

---

### Task 13: `POST /api/discover/mapping` (new)

**Files:**
- Modify: `src/HazardRecon.Web/Program.cs`
- Test: new tests appended to `tests/HazardRecon.Tests/Web/UploadEndpointTests.cs`

**Interfaces:**
- Consumes: `JobState.MappableFiles`, `MappableSetFiles` (Task 11); `IColumnMappingStore` (Task 9); `ColumnMap`, `SetColumnMaps` (Task 2).
- Produces: `POST /api/discover/mapping` endpoint; populates `job.ColumnMaps`, consumed by Task 14.

- [ ] **Step 1: Write the failing tests**

Append to `tests/HazardRecon.Tests/Web/UploadEndpointTests.cs`:

```csharp
    [Fact]
    public async Task TestConfirmingAMappingSavesItForReuse()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent discoverForm = new();
        AddFullSet(discoverForm, 0, "JUN2026.csv");
        HttpResponseMessage discoverResponse = await client.PostAsync("/api/discover", discoverForm);
        string discoverBody = await discoverResponse.Content.ReadAsStringAsync();

        using JsonDocument doc = JsonDocument.Parse(discoverBody);
        string runId = doc.RootElement.GetProperty("run_id").GetString()!;
        string setKey = doc.RootElement.GetProperty("mapping")[0].GetProperty("key").GetString()!;

        var mappingBody = new
        {
            run_id = runId,
            sets = new[]
            {
                new
                {
                    key = setKey,
                    writeoff = new Dictionary<string, string>
                    {
                        ["LoanAccountNumber"] = "LoanAccountNumber", ["CustomerId"] = "CustomerId",
                        ["Amount"] = "Amount", ["ReportDate"] = "ReportDate"
                    },
                    exposure = new Dictionary<string, string> { ["LoanAccountNumber"] = "0", ["AmountOutstanding"] = "2" }
                }
            }
        };

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/discover/mapping", mappingBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEmpty(_factory.MappingStore.RunMappings);
        Assert.Contains(_factory.MappingStore.RunMappings, m => m.FileKind == "writeoff" && m.Mapping["Amount"] == "Amount");
        Assert.Contains(_factory.MappingStore.RunMappings, m => m.FileKind == "exposure" && m.Mapping["LoanAccountNumber"] == "0");
        // the saved profile is also updated, so a future upload with this column shape reuses it
        Assert.NotEmpty(_factory.MappingStore.Saved);
    }

    [Fact]
    public async Task TestConfirmingAMappingForAnUnknownRunIs404()
    {
        HttpClient client = _factory.CreateClient();

        var mappingBody = new { run_id = Guid.NewGuid().ToString(), sets = Array.Empty<object>() };

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/discover/mapping", mappingBody);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
```

Add `using System.Net.Http.Json;` and `using System.Text.Json;` to the top of the file if not already present (`System.Text.Json` likely needs adding; `System.Net.Http.Json` for `PostAsJsonAsync`).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "TestConfirmingAMapping"`
Expected: FAIL — 404 for both (endpoint does not exist yet).

- [ ] **Step 3: Add the endpoint**

In `src/HazardRecon.Web/Program.cs`, immediately after the `/api/discover` endpoint block:

```csharp
// POST /api/discover/mapping - persists the user-confirmed column mapping for
// each set's write-off/exposure files: an audit row per run+set+file, and an
// upserted reusable profile keyed by the file's column signature so the same
// export format does not need re-mapping next time. Stashes the confirmed
// maps into JobState so /api/run can hand them to the engine directly.
app.MapPost("/api/discover/mapping", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    string bodyStr = await reader.ReadToEndAsync();
    using var doc = JsonDocument.Parse(bodyStr);

    string? rid = doc.RootElement.TryGetProperty("run_id", out var rProp) ? rProp.GetString() : null;

    Guid? userId = SupabaseJwt.UserId(ctx.User);
    if (userId == null) return Results.Unauthorized();

    if (string.IsNullOrEmpty(rid) || !jobs.TryGetValue(rid, out var job) || job.UserId != userId.Value)
        return Results.NotFound(new { error = "Unknown run - please run discovery again." });

    if (!doc.RootElement.TryGetProperty("sets", out JsonElement setsElem) || setsElem.ValueKind != JsonValueKind.Array)
        return Results.BadRequest(new { error = "Missing 'sets'." });

    Guid runGuid = Guid.Parse(rid);

    foreach (JsonElement setElem in setsElem.EnumerateArray())
    {
        string key = setElem.GetProperty("key").GetString() ?? "";
        if (!job.MappableFiles.TryGetValue(key, out MappableSetFiles? files)) continue;

        Dictionary<string, string> writeoffMapping = ReadMapping(setElem, "writeoff");
        Dictionary<string, string> exposureMapping = ReadMapping(setElem, "exposure");

        await columnMappingStore.RecordRunMappingAsync(runGuid, key, "writeoff", writeoffMapping);
        await columnMappingStore.RecordRunMappingAsync(runGuid, key, "exposure", exposureMapping);

        CsvSniff writeoffSniff = CsvSniffer.Sniff(files.WriteOffPath);
        CsvSniff exposureSniff = CsvSniffer.Sniff(files.ExposurePath);
        string writeoffSignature = ColumnSignature.Compute(writeoffSniff.Headers, writeoffSniff.SampleRows);
        string exposureSignature = ColumnSignature.Compute(exposureSniff.Headers, exposureSniff.SampleRows);

        await columnMappingStore.SaveMappingAsync(userId.Value, "writeoff", writeoffSignature, writeoffMapping);
        await columnMappingStore.SaveMappingAsync(userId.Value, "exposure", exposureSignature, exposureMapping);

        job.ColumnMaps[key] = new SetColumnMaps(
            new ColumnMap(files.WriteOffHasHeaders, writeoffMapping),
            new ColumnMap(files.ExposureHasHeaders, exposureMapping));
    }

    return Results.Ok(new { ok = true });

    static Dictionary<string, string> ReadMapping(JsonElement setElem, string fileKind)
    {
        Dictionary<string, string> mapping = new();
        if (setElem.TryGetProperty(fileKind, out JsonElement fileElem) && fileElem.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in fileElem.EnumerateObject())
            {
                string? value = prop.Value.GetString();
                if (!string.IsNullOrEmpty(value)) mapping[prop.Name] = value;
            }
        }
        return mapping;
    }
}).RequireAuthorization();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "TestConfirmingAMapping"`
Expected: PASS.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: PASS, everything from Tasks 1-12 plus these 2 new tests.

- [ ] **Step 6: Commit**

```bash
git add src/HazardRecon.Web/Program.cs tests/HazardRecon.Tests/Web/UploadEndpointTests.cs
git commit -m "feat: add POST /api/discover/mapping to persist confirmed column mappings"
```

---

### Task 14: Wire `/api/run` to use the confirmed column maps

**Files:**
- Modify: `src/HazardRecon.Web/Program.cs`
- Test: new test appended to `tests/HazardRecon.Tests/Web/UploadEndpointTests.cs` (or a new small integration test file if that file is getting unwieldy - reviewer's call, not a hard requirement)

**Interfaces:**
- Consumes: `JobState.ColumnMaps` (Task 11/13); `ReconciliationEngine.Run`'s `columnMaps` parameter (Task 8).
- Produces: nothing new for later tasks - this is the last backend task in this plan.

- [ ] **Step 1: Locate the engine invocation**

In `src/HazardRecon.Web/Program.cs`, inside the `/api/run` endpoint's background `Task.Run(...)`, find:

```csharp
            ReconciliationRunResult outResult = engine.Run(
                capturedJob.Roots, capturedJob.Outdir, logger: Logger,
                analyze: analyst != null, analyst: analyst, stages: stages);
```

- [ ] **Step 2: Pass the stashed maps through**

Replace it with:

```csharp
            ReconciliationRunResult outResult = engine.Run(
                capturedJob.Roots, capturedJob.Outdir, logger: Logger,
                analyze: analyst != null, analyst: analyst, stages: stages,
                columnMaps: capturedJob.ColumnMaps);
```

(`capturedJob.ColumnMaps` is `Dictionary<string, SetColumnMaps>`, assignable to `Run`'s `IReadOnlyDictionary<string, SetColumnMaps>?` parameter directly — no cast needed.)

- [ ] **Step 3: Write the test proving the wiring itself does not throw**

This test's job is narrower than Task 8's: Task 8's `ReconciliationEngineMappingTests` (real `SyntheticDataFixture` input) is what proves a confirmed mapping is *read correctly* by the engine. This test proves the `/api/discover` → `/api/discover/mapping` → `/api/run` chain *threads a mapping through without throwing* — e.g. a `NullReferenceException` from `capturedJob.ColumnMaps` being wired up wrong. Every other `UploadEndpointTests` fact uses the same literal `"zipbytes"` string as the debug file, which is not a real zip — `InputDiscoverer.BuildSet`'s extraction fails, finds no `lgd_defaults.csv`, and returns `null`, so `ReconciliationEngine.Run` deterministically throws `"No analysis sets found..."`, caught by `/api/run`'s handler and surfaced as `status: "error"`. That deterministic failure mode is exactly what makes this a solid assertion: it proves the request reached and executed `engine.Run(..., columnMaps: ...)` and came back through the normal error path, not a 500 from the new parameter.

Append to `UploadEndpointTests.cs`:

```csharp
    [Fact]
    public async Task TestRunningWithAConfirmedMappingWiresColumnMapsWithoutThrowing()
    {
        HttpClient client = _factory.CreateClient();

        using MultipartFormDataContent discoverForm = new();
        AddFullSet(discoverForm, 0, "JUL2026.csv");
        HttpResponseMessage discoverResponse = await client.PostAsync("/api/discover", discoverForm);
        using JsonDocument discoverDoc = JsonDocument.Parse(await discoverResponse.Content.ReadAsStringAsync());
        string runId = discoverDoc.RootElement.GetProperty("run_id").GetString()!;
        string setKey = discoverDoc.RootElement.GetProperty("mapping")[0].GetProperty("key").GetString()!;

        var mappingBody = new
        {
            run_id = runId,
            sets = new[]
            {
                new
                {
                    key = setKey,
                    writeoff = new Dictionary<string, string>
                    {
                        ["LoanAccountNumber"] = "LoanAccountNumber", ["CustomerId"] = "CustomerId",
                        ["Amount"] = "Amount", ["ReportDate"] = "ReportDate"
                    },
                    exposure = new Dictionary<string, string> { ["LoanAccountNumber"] = "0", ["AmountOutstanding"] = "2" }
                }
            }
        };
        HttpResponseMessage mapResponse = await client.PostAsJsonAsync("/api/discover/mapping", mappingBody);
        Assert.Equal(HttpStatusCode.OK, mapResponse.StatusCode);

        HttpResponseMessage runResponse = await client.PostAsJsonAsync("/api/run", new { run_id = runId });
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);

        // poll briefly - the engine runs on a background Task.Run
        JsonElement job = default;
        for (int i = 0; i < 50; i++)
        {
            HttpResponseMessage jobResponse = await client.GetAsync($"/api/job/{runId}");
            Assert.Equal(HttpStatusCode.OK, jobResponse.StatusCode);
            using JsonDocument jobDoc = JsonDocument.Parse(await jobResponse.Content.ReadAsStringAsync());
            job = jobDoc.RootElement.Clone();
            if (job.GetProperty("status").GetString() != "running") break;
            await Task.Delay(50);
        }

        // deterministic given the placeholder "zipbytes" debug file: discovery
        // finds zero valid sets, so the engine throws before ever reaching
        // DataLoaders - reaching this exact status, rather than a 500, proves
        // engine.Run(..., columnMaps: capturedJob.ColumnMaps) is wired correctly
        Assert.Equal("error", job.GetProperty("status").GetString());
        Assert.Contains("No analysis sets found", job.GetProperty("error").GetString());
    }
```

- [ ] **Step 4: Run the test**

Run: `dotnet test --filter TestRunningWithAConfirmedMappingWiresColumnMapsWithoutThrowing`
Expected: PASS.

- [ ] **Step 5: Run the full suite one final time**

Run: `dotnet test`
Expected: PASS — every test from Tasks 1-14, full green build.

- [ ] **Step 6: Commit**

```bash
git add src/HazardRecon.Web/Program.cs tests/HazardRecon.Tests/Web/UploadEndpointTests.cs
git commit -m "feat: pass the confirmed column mapping into the engine at /api/run"
```

---

## Verification (after all 14 tasks)

- [ ] `dotnet build` — clean, no warnings about unused `UploadReceiver`/`UploadPath` references remaining anywhere.
- [ ] `dotnet test` — full suite green.
- [ ] Apply the new migration to a local Supabase instance (`npx supabase@latest db reset`) and confirm `\dp public.saved_column_mappings` / `\dp public.run_set_column_mappings` show real grants, not just `Dxtm`.
- [ ] Manually drive `/api/discover` → `/api/discover/mapping` → `/api/run` with `curl` against the running app and a real (or the `SyntheticDataFixture`-shaped) write-off/exposure pair with deliberately renamed/headerless columns, confirming the run completes and its `untraced`/`trace_rate` figures match what the same data would produce under its original column names — this is the true end-to-end proof the unit/integration tests above only approximate with small inline CSVs.
