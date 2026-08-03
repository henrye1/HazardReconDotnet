# Column mapping for uploaded write-off / IFRS9 files

**Date:** 2026-08-03
**Status:** draft, pending review

## Problem

The web upload flow (`wwwroot`'s folder picker → `POST /api/discover` →
`UploadReceiver` → `InputDiscoverer`) requires each picked folder to contain files
`InputDiscoverer` can recognize by name/shape, and the write-off and IFRS9 CSVs must
carry specific header names for `DataLoaders` to read them at all:

- Write-off: `LoanAccountNumber`, `CustomerId`, `Amount`, `ReportDate`
  (`DataLoaders.cs:275-280`).
- IFRS9: `LoanAccountNumber`, `AmountOutstanding`, read via the generic
  `LoadSourceAccounts(path, colName, label, amountCol, log)` helper, called as
  `LoadSourceAccounts(setInfo.Ifrs9, "LoanAccountNumber", ..., "AmountOutstanding",
  log)` (`ReconciliationEngine.cs:238`).

A bank's actual export rarely matches this exactly — different header names, or no
header row at all — and today that fails silently: `csv.GetField(name)` returns
null, the account is skipped, and the run under-counts with no visible error.

A UI mockup (`Flow-based risk suite redesign/Hazard-Rate Reconciliation.html`, a
Claude-Artifacts bundle) redesigns the input step around this problem: instead of a
folder pick, the user uploads four individual files per set (exposure/IFRS9,
write-off, debug, scenario), and for the two CSVs, confirms a column mapping —
AI-guessed where possible, with a confidence score — before the file is used. The
mockup is a pure UI prototype: no real `<input type="file">`, no API calls, no
persistence. This spec designs the real thing.

## Goals

- Replace the folder-picker upload with four explicit per-set file slots (exposure,
  write-off, debug, scenario), matching the mockup's `FILE_KINDS`.
- For write-off and exposure, let the user confirm which uploaded column maps to
  each field the engine needs, with an AI-assisted guess and a confidence score.
- Apply the confirmed mapping server-side, so the uploaded file travels unmodified
  and `DataLoaders` resolves columns through the mapping instead of hardcoded names.
- Remember a confirmed mapping by the shape of the file (not its filename), so the
  same export format from the same source system doesn't need re-mapping next run.
- Keep the engine (`HazardRecon.Core`) and the CLI untouched.

## Non-goals

- **Changing the CLI.** `HazardRecon.Cli` never sends a mapping; `DataLoaders`
  defaults to today's literal header names when none is supplied.
- **Mapping the debug or scenario files.** Their internal structure is fixed
  (`debug.zip`'s contents, `scenario.json`'s keys) — nothing to map.
- **Sharing saved mappings across users.** Scoped per `user_id`, consistent with
  every other table in the schema — no organization/tenant concept exists.
- **Client-side CSV rewriting.** The uploaded file is stored and read as-is; see
  "Why server-side" below.
- **A general-purpose mapping UI for arbitrary future file kinds.** This is scoped
  to the two known CSVs; if a third mappable file type shows up later, revisit.

## Why server-side, not client-side

Two ways to apply a mapping: rewrite the CSV's header row in the browser before
upload, or upload the file as-is and resolve columns during parsing. Rejected the
client-side rewrite: write-off exports run 150k+ rows / 18+ MB, and rewriting that
in browser JS before every upload is real client-side work for no benefit — the
server-side path leaves the file byte-identical to what the user has (useful for
support/debugging), and makes the confirmed mapping a real, storable, auditable
fact rather than something invisibly baked into a client-side transform.

## Architecture

```
browser                                  .NET (Web)                    LLM gateway
-------                                  ----------                    -----------
Files (step 0)
  pick 4 files x up to 4 sets

  -- advance to Map columns --
  POST /api/discover  ------------->  create run, store files,
  (multipart, tagged by set+kind)     read header + samples for
                                      writeoff/exposure per set
                                            |
                                            | column signature lookup
                                            | (saved_column_mappings)
                                            |
                                            | miss? ------------------->  guess mapping
                                            |                             + confidence
                                            |  <--------------------------
  <---------------------------------  per-file columns + suggested
  render Map columns (step 1)         mapping (MAP_SPEC-shaped)

  user reviews/edits, confirms

  POST /api/discover/mapping  ----->  persist mapping (audit +
  {run_id, sets:[...]}                upsert saved profile),
                                      stash into JobState,
  <---------------------------------  return final inventory/problems
  render Confirm (step 2)

  POST /api/run  (unchanged) ------>  engine.Run(..., columnMap: ...)
                                      DataLoaders resolves columns
                                      via the mapping, not literals
```

The mapping never touches `HazardRecon.Core`'s public engine surface as a new
concept — it resolves down to "here is the literal column name (or index) for each
field," which is exactly what `DataLoaders` already looks up, just no longer
hardcoded.

## Data model

Two new tables, following the fully-relational convention the rest of the schema
already uses (see `supabase/migrations/20260731000000_normalize_run_results.sql`) — both are small,
fixed-shape (2 file kinds × ~4 fields), so neither is a candidate for jsonb the way
`run_set_engine_params` was.

### `public.saved_column_mappings`

The reusable profile — looked up before falling back to an AI guess.

```sql
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
```

`source_column` is either a header name (file has headers) or a stringified
0-based column index (file does not) — see "Column resolution" below for why one
column serves both cases.

### `public.run_set_column_mappings`

What was *actually* used for this run, for audit/debugging — distinct from the
saved profile, which can drift (a user edits it) after the run that used the old
version.

```sql
create table public.run_set_column_mappings (
  id             bigint generated always as identity primary key,
  run_id         uuid not null references public.runs(id) on delete cascade,
  set_key        text not null,
  file_kind      text not null check (file_kind in ('writeoff', 'exposure')),
  field_name     text not null,
  source_column  text not null,
  unique (run_id, set_key, file_kind, field_name)
);
```

### RLS

`saved_column_mappings` carries `user_id` directly (Pattern A from the prior
redesign): `for select to authenticated using (auth.uid() = user_id)`.
`run_set_column_mappings` has none of its own (Pattern B): join through `runs`,
`exists (select 1 from public.runs r where r.id = run_set_column_mappings.run_id
and r.user_id = auth.uid())`. Same defense-in-depth caveat as everything else —
the server writes as `service_role`, bypassing RLS.

## Column signature

Computed server-side from the uploaded file, independent of filename:

- **Headers present:** hash of the lowercased, ordered header list.
- **No header row:** hash of `(column count, per-column value-shape
  classification)` sampled from the first ~200 data rows, where the shape
  classifier buckets a column as one of `numeric | date | currency | text` (cheap
  regex/parse checks, not an LLM call). Two files from the same source system's
  export produce the same signature even though neither has header names to
  compare; two structurally different files don't collide just because they
  happen to share a row count.

The signature is a stored `text` column, not a separate hash function choice this
spec needs to pin down precisely — SHA-256 of the canonical string form is
sufficient and matches nothing else in the schema needing to reverse it.

## Column resolution (why `source_column` is one text column)

A field's resolved location is either "the column named X" (headered file) or
"the column at index N" (headerless file) — never both for the same file, since
whether the file has a header row is a property of the file, known once at mapping
time. Rather than a nullable-pair struct, `source_column` stores the header name
when the file has headers, or the index as a string when it doesn't; `DataLoaders`
already knows (from the same detection that built the mapping UI) whether to treat
the file as headered, so there's no ambiguity to resolve at read time. Internally,
resolution needs a `HasHeaderRecord` toggle on `CsvHelper`'s configuration
(currently implicit `true` everywhere) and a small helper that reads a mapped
reference as either `csv.GetField(name)` or `csv.GetField(int.Parse(name))`.

## Column guessing (LLM-assisted)

New service, alongside `AiAnalysisService` in `HazardRecon.Core.Services` (or a
small new file in the same area) — takes the header row (nullable), a handful of
sample rows, and the target field list (name + the descriptive note already
established, e.g. "Normalised and used as the join key against defaults and
exposure"), and calls `ILlmClient.ChatAsync` with a prompt requesting a bare JSON
object back: `{"FieldName": {"column": "...", "confidence": 0.97}, ...}`.

Same defensive shape as `AiAnalysisService.GenerateAnalysis` (`AiAnalysisService.cs:107-141`):
any exception, empty response, or unparseable JSON just means no guess for that
field — never blocks the flow, the user maps it by hand. Uses a fixed internal
model id, independent of whatever model the user later picks for the AI-analysis
narrative at the Run step (model selection today happens at `/api/run`, which is
after mapping — there is no model chosen yet at this point in the flow).

**Resolution order per field**, applied when building the Map-columns response:
1. Exact header-name match (file has headers, and one matches the field's known
   name/aliases) → no AI call needed, "Header match".
2. A `saved_column_mappings` row exists for this `(user_id, file_kind,
   column_signature)` → reuse it, "Set by you" (from a prior run).
3. LLM guess → "AI NN%".
4. None of the above → unmapped, user must pick from the column list.

## API changes

### `POST /api/discover` (redesigned body)

Multipart, fields named `set{N}.{kind}` (`N` from 0 to 3, `kind` one of `exposure`,
`writeoff`, `debug`, `scenario`). Exactly one file per kind, except `debug`, which
accepts one (`debug.zip`) or three (`lgd_defaults.csv`, `pd_scored.csv`,
`debug.json`) — matching what `InputDiscoverer` already accepts today, just
re-plumbed onto explicit tagging instead of folder-content guessing.

Since each file's role is now stated by the client rather than guessed from a
folder, the web path bypasses `InputDiscoverer`'s name/shape heuristics entirely
and constructs the `InventorySet` directly from the four tagged files — that
guessing logic stays exactly as-is for the CLI's folder-based discovery, untouched.

Response: `run_id` plus, per set, header row + sample rows for `writeoff` and
`exposure` (nothing returned for `debug`/`scenario` — there's nothing to map), each
already annotated with a resolved mapping per the order above, in the shape the
Map-columns step renders directly (mirrors the mockup's `MAP_SPEC`).

A set's label defaults to the `exposure` file's name without its extension
(my call — parallels today's "the folder name is the label" convention) and is
editable by the user; a name field is a small addition to the mockup's Files step,
not present in the prototype as given.

### `POST /api/discover/mapping` (new)

Body: `{run_id, sets: [{key, mappings: {writeoff: {field: column, ...}, exposure:
{field: column, ...}}}]}` — only the two mappable kinds appear.

Persists the confirmed mapping: inserts into `run_set_column_mappings` (audit,
replacing any prior rows for this run+set+kind — a run can be re-mapped and
re-triggered) and upserts into `saved_column_mappings` (`on conflict (user_id,
file_kind, column_signature, field_name) do update set source_column = excluded.
source_column, last_used_at = now()`). Stashes the confirmed mapping into the
in-memory `JobState` for this run — the same pattern `job.Roots` already uses — so
`/api/run` can hand it to the engine without a DB read at run time.

Response: final inventory + problems (row counts, obvious data issues now that
columns are known) — renders the Confirm step.

### `/api/run`

Unchanged trigger shape (`{run_id, model_id}`). Internally, `engine.Run(...)` gains
an optional column-map parameter threaded down to `DataLoaders`, sourced from
`job`'s stashed mapping.

## `DataLoaders.cs` changes

`LoadWriteoff` and `LoadSourceAccounts` take an optional column map (a small
per-file `IReadOnlyDictionary<string, string>` — field name to `source_column`,
same shape as what's stored), defaulting to null. Null means "use the literal
field name as the column reference," which is exactly today's behavior — the CLI
path is unaffected since it never supplies one. `CsvConfig`'s `HasHeaderRecord`
becomes a parameter too (currently implicit), set by whichever caller already
knows the file's header-presence from the mapping step.

## Frontend

`wwwroot`'s folder-picker (`webkitdirectory`) input is replaced with four real
`<input type="file">` slots per set, matching `FILE_KINDS`, with an "Add another
set" control (up to 4, matching today's `UploadReceiver.MaxSets`). The Map-columns
step is a table per CSV file (per the mockup's layout): field name, note, a status
pill (Needs mapping / Set by you / AI NN% / Header match, with the mockup's tone
colors), a sample value, and a `<select>` of that file's columns. "Confirm mapping"
is disabled while any field is unmapped, matching the mockup's `mapBlocked` gate.

## Testing

- **Column signature**: same header list (any case) → same signature; different
  header order → different signature (order matters, since `GetField` reads a
  header-based file by name regardless of position, but changing order is still a
  meaningful "this looks like a different export" signal worth a fresh guess);
  headerless files with the same shape classification → same signature.
- **Column guessing service**: `FakeLlmClient` (already used by
  `AiAnalysisServiceTests`-style tests) returning valid JSON, malformed JSON, and a
  thrown exception — all three must degrade to "no guess," never throw.
- **`DataLoaders` with a mapping**: headered file with a non-default mapping,
  headerless file resolved by index, and the no-mapping/null case reproducing
  today's exact behavior (regression guard for the CLI path).
- **`POST /api/discover` / `POST /api/discover/mapping`**: `WebApplicationFactory`
  integration tests in the style of `UploadEndpointTests.cs`, using
  `FakeRunStore`/`FakeLlmClient`, asserting the resolution order (header match >
  saved mapping > AI guess > unmapped) and that a saved mapping actually gets
  reused on a second upload with the same signature.

## Suggested build order

1. Migration: `saved_column_mappings`, `run_set_column_mappings` + RLS.
2. `DataLoaders.cs`: optional column map + `HasHeaderRecord` parameter, defaulting
   to today's behavior — land this alone first, it's a pure refactor with no
   behavior change when the parameter is omitted.
3. Column signature + guessing service, unit-tested against `FakeLlmClient`.
4. `POST /api/discover` redesign (multipart shape, direct `InventorySet`
   construction, header/sample response).
5. `POST /api/discover/mapping` (persistence + `JobState` stash + final
   inventory).
6. Wire `/api/run` to pass the stashed mapping through to the engine.
7. Frontend: file slots, Map-columns table, Confirm step.

## Open risks

- **LLM guess quality/cost on every discover call.** Two files, ~4 fields total,
  small sample — should be cheap and fast, but worth confirming the gateway's
  latency doesn't make the Map-columns step feel slow; no caching beyond the saved
  mapping itself is planned.
- **Column signature collisions across genuinely different files that happen to
  classify the same (e.g. two headerless 4-column files, one shaped account/date/
  amount/text and another shaped the same way but meaning something else).** The
  shape classifier is a heuristic, not a guarantee — a wrong reuse is silently
  wrong in the same way a wrong AI guess would be, just skips the confidence
  signal. Mitigate by always showing the resolved mapping (even a reused one) in
  the Map-columns step rather than auto-skipping it — the user still sees and can
  correct it before confirming.
