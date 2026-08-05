# Run again: edit a past run's inputs

**Date:** 2026-08-05
**Status:** draft, pending review

## Problem

"Run again" on the run detail screen starts a reconciliation immediately. Both it
and the confirm step's "Run reconciliation" are wired to the same handler
(`app.js:1314-1315`), so `beginRun` (`app.js:1165`) jumps the wizard to the run
step and posts `/api/run` with the current `RUN_ID`.

That leaves no way to re-run with one file swapped. The common case is exactly
that: the same period re-reconciled after a corrected write-off export, or the
same debug file against a revised exposure file. Today it means a new run from
scratch — re-picking and re-uploading all four files, including a debug file that
is routinely 100-200 MB.

The obvious client-side fix does not work. A browser cannot refill an
`<input type="file">` from script, so once the page reloads or the user reopens a
run from history, the `File` objects are gone.

But the files are not gone. `/api/discover` writes each set's uploads to
`runs/<rid>/input/<setIndex>/` under canonical names (`SetFileReceiver.cs:86-99`)
and then persists that directory to object storage, indexing every file in
`run_files` (`Program.cs:254`). Rows carry `set_key`, `relative_path`,
`storage_path` and `size_bytes` (`RunFileRecord.cs`). Inputs survive 30 days,
after which `InputPurger` deletes them and stamps `runs.inputs_purged_at`
(`InputPurger.cs:15,50`; `RunRecord.cs:35`) — a flag added, per its own comment,
"so the UI can say the inputs have expired".

Nothing exposes them. `RunDetailAssembler` returns outputs only, and `IFileStore`
(`Files/IFileStore.cs`) can upload, sign and delete but not read back.

## Goals

- "Run again" opens the Files step with the run's stored inputs shown on the
  cards — role, original filename, size — each with its existing Replace and
  remove controls.
- Replacing one file re-uploads only that file. The rest are reused from the
  previous run's stored objects.
- A run whose inputs have been purged says so, and asks for the files again.
- Changing any file forces a re-check before the run can start, so the result can
  never be produced from inputs the screen is no longer showing.
- The confirm step's "Run reconciliation" behaves exactly as it does now.

## Non-goals

- **Changing the engine or the CLI.** Reuse is resolved into the new run's input
  directory before discovery; `HazardRecon.Core` sees an ordinary directory of
  files, as it does today.
- **Editing a completed run in place.** Re-running always mints a new run id and
  a new history entry. The previous run and its outputs are untouched.
- **Recovering inputs after the 30-day purge**, or changing that window. A
  purged run asks for its files again.
- **Sharing one stored copy between runs.** Each run keeps its own inputs; see
  "Risks" for why.
- **Reusing inputs across users.** Every read is scoped by `user_id`, as
  `IRunFileStore.ListAsync` already requires.

Adding a set on a re-run is *not* excluded: a set index with no counterpart in
the previous run simply reuses nothing and is validated on its own, so "Add
another set" keeps working. Clearing slots keeps working too, since that is only
emptying them.

## Why the reuse is server-side

Three ways to avoid re-uploading:

1. **Client holds the files.** Only works while the page is open. Fails for the
   case that motivated this — reopening a run from history — so it is not a
   solution, just an optimisation of one path.
2. **Client downloads the stored inputs, then re-uploads them.** Works, but
   moves every unchanged byte twice over the user's connection to achieve
   "without re-uploading the rest". Self-defeating.
3. **Server copies its own stored objects into the new run.** The bytes never
   leave the server. Chosen.

## Design

### Migration

`run_files` gains two nullable columns, meaningful for `kind = 'input'` rows:

- `original_name text` — the filename the user picked. `SetFileReceiver` renames
  exposure, write-off and scenario files to canonical names, so without this the
  cards can only say `IFRS9.csv` and `writeoff.csv`, which identify neither
  period nor source.
- `role text` — `exposure` | `writeoff` | `debug` | `scenario`.

Role is today only *implied*, by the canonical name the receiver chose, with
"anything else in the set folder" meaning debug. That rule is correct but
implicit and would have to be re-derived by every reader. Recording it is cheaper
than inferring it and cannot drift from the receiver's naming.

Rows written before this migration have both null. Readers fall back to the
canonical name for display and to the name-derived role, so existing history
keeps working — with canonical names on the cards.

### `GET /api/runs/{rid}/inputs`

Returns, scoped to the calling user:

```
{ "inputs_purged": false,
  "sets": [ { "set_key": "...", "label": "...",
              "files": [ { "role": "exposure", "name": "IFRS9 FILE JUNE 2025.csv",
                           "size_bytes": 12812345 } ] } ] }
```

`inputs_purged` comes from `runs.inputs_purged_at`. A purged run returns no
files. An unknown run, or one belonging to another user, is a 404 — matching
`/api/run`'s existing choice of 404 over 403 so a 403 cannot confirm existence.

Only `kind = 'input'` rows are read. Usefully, those are the picked files and
nothing else: persistence (`Program.cs:254`) runs *before* `BuildSet` unzips the
debug file into `_extracted` (`InputDiscoverer.cs:117-121`), so the extracted
contents were never indexed and cannot leak into the cards. Re-running relies on
the same ordering — the reused `debug.zip` is extracted afresh by the new run's
discovery.

### `/api/discover` accepts `based_on_run`

A new optional form field carrying a previous run id. Uploaded files are received
exactly as now (`Program.cs:190-200`). Then, for each set index, every role that
the previous run had and this upload does *not* is materialised into the new
run's input directory by downloading that run's stored object and writing it
under the same canonical name the receiver would have used.

Resolution happens before `InputDiscoverer` runs, so discovery, sniffing, column
mapping and the engine are unchanged — they see a complete input directory.

Failure modes:

- `based_on_run` unknown, or another user's: 404, as above.
- Its inputs are purged, or a needed object is missing from storage: 400 naming
  the roles that must be picked again, so the client can mark exactly those
  slots. Not a 500 — the user has an action.
- A set index in the upload with no counterpart in the previous run: reuse
  nothing for it and require it to be complete on its own, which the existing
  per-set validation already enforces.

### `IFileStore.DownloadToFileAsync`

New method plus its Supabase implementation. The engine reads inputs from local
disk (`job.Roots`, `Program.cs:244,516`), so a reused object has to be
materialised locally; an in-bucket copy would not be enough.

It writes to a path rather than returning a `Stream`, because
`SupabaseRestClient.SendAsync` reads whole responses into a `string`
(`SupabaseRestClient.cs:43`) and a debug file of several hundred megabytes must
not be buffered. A companion `SupabaseRestClient.DownloadToFileAsync` streams
with `HttpCompletionOption.ResponseHeadersRead`, and writes nothing unless the
response succeeded, so a failure cannot leave a truncated file that would read as
a valid but short input.

### Reused files are handed to the receiver as upload items

`InputReuse` turns each reused role into an ordinary `SetFileItem` and passes it
to `SetFileReceiver` alongside the real uploads, rather than writing into the
input directory itself.

`SetFileReceiver` then needs no change at all: canonical naming, the
"missing its exposure file" check, the per-set size limit and the set label —
which is taken from the exposure file's original name — all keep working for a
set that was mostly reused. A second path that wrote files directly would have to
reimplement each of those and could drift from the first.

The reused bytes are downloaded to `runs/<rid>/_reuse/`, which sits *outside*
`input/`, so it is never persisted as part of the new run. It is deleted once the
receiver has copied from it.

### Client: slots hold either a File or a stored-file descriptor

`SETS[i].files[kind]` is already an array per role, and may now hold
`{ name, size, fromRun }` descriptors instead of `File`s. `slotSub`
(`app.js:564`) reads only `.name` and `.size`, so both render identically with no
change to it — including a multi-file debug slot, whose several descriptors
summarise through the same "N files · size" branch. Replace and the remove button
already only assign to `set.files[kind]`, so acting on a descriptor turns that
slot back into an ordinary pick with no special case.

A slot is either all descriptors or all `File`s, never mixed: Replace overwrites
the whole array, which is what the picker already does.

`setBytes` counts descriptors along with picked files, and so does the size-limit
check. A reused file is not being uploaded, so counting it as upload cost looks
wrong at first — but the limit `SetFileReceiver` enforces is on the *whole set*,
and reused files reach it as items like any other. Counting only the new bytes in
the browser would let a set through that the server then rejects, after the
upload had been paid for. One rule, enforced the same way on both sides.

`discover()` (`app.js:728`) appends only real `File`s, and adds two fields when
any descriptor survives: `based_on_run` (the run the descriptors came from) and
`reuse`, a JSON array of `{ set, roles }` naming exactly what the server should
rebuild. Sending the roles explicitly rather than letting the server infer
"whatever was not uploaded" is what makes removing a file work: a role the user
cleared is simply absent from both.

### Client: "Run again"

`#btn-rerun` gets its own handler; `#btn-run` keeps `beginRun` untouched.

The handler shows the wizard, fetches `/api/runs/{rid}/inputs`, builds `SETS`
from the response, and calls `setStep(0)`. On `inputs_purged`, it builds empty
sets and shows a line saying the inputs expired after 30 days and need choosing
again.

### Client: a changed file closes the later steps

Landing on Files with the previous run's mapping and confirmation still reachable
is what makes "change nothing, just re-run" cheap: the rail can go straight to
confirm and re-run the existing run id with no upload at all.

That is only safe while the files on screen are the files the server holds. The
first change to any slot therefore resets `STEP_REACHED` to 0 (`app.js:207-227`
owns the rail's reachability), closing mapping, confirm and run until "Check
columns" is pressed again. Without this, replacing a file and then jumping
forward would run the *previous* upload while the screen showed the new file —
the same class of quietly-wrong result as the 0% trace this follows.

## Testing

**.NET**

- `original_name` and `role` are persisted for input files, and readers fall back
  correctly when both are null.
- `/api/runs/{rid}/inputs`: shape; another user's run is 404; a purged run
  reports `inputs_purged` with no files.
- `/api/discover` with `based_on_run`: all roles reused when nothing is uploaded;
  only the replaced role taken from the upload and the rest reused; a purged
  source gives a 400 saying the files expired; another user's run is a 404.
- `InputReuse`: a requested role the previous run does not have, and an object
  that is indexed but missing from the bucket, are both refused by name.
- `DownloadToFileAsync` round-trips a stored object, and a 404 from storage
  leaves no file behind.

**Client harness** (`tests/client/app.harness.mjs`)

- Run again lands on step 0 with every slot named and sized from the response.
- Run again on a purged run shows the expired line and empty slots.
- Touching any slot closes mapping, confirm and run; leaving them untouched keeps
  them reachable.
- `discover()` posts `based_on_run` and `reuse` plus only the replaced file, and
  posts neither field once every slot in the set has been replaced.
- Adding a set on a re-run works: the new set is uploaded in full and named in no
  reuse entry.
- "Run reconciliation" on the confirm step still starts a run, unchanged.

## Risks

- **A reused file is not re-validated against its saved column mapping.** The
  mapping is keyed on column signature, and a reused file's columns cannot have
  changed, so the signature still matches. Discovery re-sniffs it regardless,
  because it is re-read from disk like any other input.
- **Storage cost of a re-run is a full second copy of the inputs.** Each run owns
  its input prefix and its own 30-day window; sharing one copy between runs would
  make purging the first run corrupt the second. Accepted deliberately.
- **Old runs show canonical filenames.** Unavoidable — the originals were never
  recorded. Only affects runs created before the migration.
