# Supabase auth + run persistence

**Date:** 2026-07-30
**Status:** approved, not yet implemented

## Problem

The web app is a single-user local tool wearing a browser UI. It has no concept of
a user, and nothing it produces survives a restart:

- **Inputs are server-local folder paths.** The user pastes `C:\...\DEBUG FILE 30
  JUNE 2026`, and `InputDiscoverer` reads those files in place
  (`index.html:27` — "Nothing is copied — files are read where they are").
- **Runs live in a `ConcurrentDictionary`** (`Program.cs:32`). A restart loses every
  run, its log, its results, and its chat payload.
- **Outputs are written next to the binary**, under `AppContext.BaseDirectory/runs`
  (`Program.cs:28-30`).
- **There is no authentication anywhere.** Every endpoint is open.

We are deploying to Render. Each of those three facts breaks there: nobody can
paste a path to a folder on Render's disk, that disk is wiped on every deploy, and
a public URL with no login is open to the internet.

## Goals

- Email/password login via Supabase, open signup with email confirmation.
- Users upload their analysis folders instead of naming server paths.
- Every run — metadata, log, per-set summaries, output artifacts, uploaded inputs,
  and chat history — persists and is browsable after a restart.
- Each user sees only their own runs.
- Reach all of this without changing `HazardRecon.Core`.

## Non-goals

- **Changing the CLI.** `HazardRecon.Cli` stays a local tool with no auth, reading
  local folders. Only the web app becomes multi-user.
- **Changing the reconciliation engine.** `InputDiscoverer`, `ReconciliationEngine`,
  and the exporters are untouched. Uploads are rehydrated into a temp folder that
  looks exactly like today's input folder, and the existing code runs against it.
- **Sharing runs between users.** Strictly private. No share links, no team
  workspace, no admin view.
- **Multi-instance Render deployment.** See "Single-instance constraint" below.
- **Realtime / websockets.** The existing poll loop stays.
- **Resumable or direct-to-storage uploads.** Sets are under 50 MB; a single
  multipart POST through the server is sufficient.
- **Migrating existing local runs.** There are none worth keeping.

## Architecture

Supabase as BaaS, .NET as the only trusted backend.

```
browser                          .NET on Render                Supabase
-------                          -------------                --------
supabase-js
  signup / login / refresh  ------------------------------->   Auth
  holds access_token

  fetch + Bearer token  ------>  JwtBearer validates
                                 against JWKS
                                       |
                                       | service-role key
                                       +-------------------->  Postgres
                                       +-------------------->  Storage
```

**The browser never talks to Postgres or Storage.** It authenticates against
Supabase Auth and then speaks only to our API. This is what removes the token
lifetime problem: a run executes in a background task that can outlive the user's
one-hour access token, and it writes with the service-role key, which does not
expire.

### Why not enforce isolation with RLS

The obvious alternative is forwarding the user's JWT to PostgREST so row-level
security enforces isolation in the database rather than in our code. Rejected: the
background run task outlives the access token, so the server would have to store
and rotate refresh tokens purely to finish uploading outputs. We get the same
protection more cheaply — see the RLS policies below, which are deny-by-default
against a leaked anon key while the server writes as service-role.

## Data model

### `public.runs`

| Column | Type | Notes |
|---|---|---|
| `id` | uuid pk | `gen_random_uuid()` |
| `user_id` | uuid not null | → `auth.users(id)` on delete cascade |
| `status` | text not null | `ready` \| `running` \| `done` \| `error` \| `interrupted` |
| `model_id` | text null | null means the run skipped AI analysis |
| `set_labels` | jsonb not null | uploaded folder names, default `[]` |
| `log` | jsonb not null | the `{t, msg, kind}` entries, default `[]` |
| `result` | jsonb null | per-set summaries + artifact filenames |
| `analysis_payload` | jsonb null | masked aggregates that feed chat |
| `error` | text null | |
| `created_at` | timestamptz not null | `now()` |
| `started_at` | timestamptz null | |
| `finished_at` | timestamptz null | |
| `inputs_purged_at` | timestamptz null | set by retention, see below |

Index on `(user_id, created_at desc)` — the run-history query.

`result` stores artifact **filenames only**, never paths. Today's values are local
paths to a temp directory that will not exist by the time anyone reads the row.

### `public.run_files`

| Column | Type | Notes |
|---|---|---|
| `id` | uuid pk | |
| `run_id` | uuid not null | → `runs(id)` on delete cascade |
| `user_id` | uuid not null | denormalised so RLS needs no join |
| `kind` | text not null | check in (`input`, `output`) |
| `set_key` | text null | null for outputs spanning all sets |
| `relative_path` | text not null | sanitised `webkitRelativePath`, or output filename |
| `storage_path` | text not null | full key in the `runs` bucket |
| `size_bytes` | bigint not null | |
| `created_at` | timestamptz not null | |

Index on `(run_id)`.

### `public.chat_messages`

| Column | Type | Notes |
|---|---|---|
| `id` | uuid pk | |
| `run_id` | uuid not null | → `runs(id)` on delete cascade |
| `user_id` | uuid not null | |
| `role` | text not null | check in (`user`, `assistant`) |
| `content` | text not null | |
| `content_html` | text null | the rendered markdown the UI already returns |
| `created_at` | timestamptz not null | |

Index on `(run_id, created_at)`.

### RLS

All three tables: `alter table ... enable row level security`, with a single
policy each:

```sql
create policy "own rows readable" on public.runs
  for select to authenticated using (auth.uid() = user_id);
```

No insert, update, or delete policies exist for any role. The server writes as
service-role, which bypasses RLS entirely.

These policies are **defense-in-depth, not the enforcement mechanism.**
Correctness rests on the server filtering by the `sub` claim of a
signature-verified token. The policies exist so that if the public anon key is
ever leaked or scraped from the browser bundle, hitting PostgREST directly
returns an empty set rather than every customer's data.

### Storage

One **private** bucket, `runs`, with no storage policies at all — only
service-role can reach it.

```
{user_id}/{run_id}/input/{set_key}/{relative_path}
{user_id}/{run_id}/output/{filename}
```

The `user_id` prefix is deliberate. It makes ownership legible in the storage
browser and makes a per-user purge a prefix delete.

Downloads never expose this bucket. `.NET` checks ownership, then mints a
60-second signed URL and redirects to it.

### Retention

Inputs are the only large objects: up to 50 MB per set, 4 sets, so 200 MB per run
against a 1 GB free tier — roughly five runs before the project wedges. Outputs
and metadata are small and are kept forever.

**Input files are purged 30 days after their run's `created_at`.** A startup task
plus a once-daily timer deletes the storage objects under
`{user_id}/{run_id}/input/`, deletes the matching `run_files` rows, and stamps
`runs.inputs_purged_at`. The history UI reads that column and shows "inputs
expired" rather than an upload-again button.

## Request flow

Every endpoint requires a valid bearer token except `/health` and `GET /api/config`.

| Endpoint | Change |
|---|---|
| `GET /api/config` | **new**, unauthenticated. Returns the Supabase URL and anon key so `app.js` hardcodes neither. |
| `POST /api/runs` | **replaces `/api/discover`, which is deleted.** Multipart upload; each part tagged with set index and `webkitRelativePath`. Creates the run row, rehydrates a temp folder, mirrors inputs to Storage, calls the existing `DiscoverFromFolders`, returns inventory + problems + log. |
| `POST /api/run` | unchanged shape; adds ownership check and persists status transitions. |
| `GET /api/job/{rid}` | unchanged shape; adds ownership check. Reads the live cache while running, the DB otherwise. |
| `GET /api/runs` | **new.** Run history for the caller: id, created_at, status, set labels, model. |
| `GET /api/runs/{rid}` | **new.** Full detail of a past run: result, downloads, chat history. |
| `POST /api/chat` | adds ownership check; persists both the question and the reply. |
| `GET /runs/{rid}/output/{filename}` | ownership check, then 302 to a signed URL. |
| `GET /api/models` | now requires auth — it costs a gateway call. |
| `GET /health` | unchanged, unauthenticated. |

### Upload handling

`webkitRelativePath` is attacker-controlled. Every incoming path is normalised and
rejected if it contains a `..` segment, is absolute, or carries a drive letter,
**before** it is used as a filesystem path or a storage key. This check is a pure
function with its own tests.

Limits enforced server-side, not just in the browser: at most 4 sets, 50 MB per
set, and at most 500 files per set.

The temp folder is created under `Path.GetTempPath()` and deleted in a `finally`.
Nothing durable ever lives on Render's disk.

### Run lifecycle and the single-instance constraint

The in-memory `ConcurrentDictionary` **stays**, but demoted to a live cache for
the currently-running job's log — otherwise every log line costs a DB round-trip.
Postgres is the source of truth, flushed on completion.

On startup, any row still marked `running` is marked `interrupted`: a deploy or
restart killed it, and nothing will ever finish it.

**This requires Render to run a single instance.** Two instances would split the
cache, so a poll routed to the wrong instance would see no log, and each instance's
startup would mark the other's live runs as interrupted. Render's scaling setting
must stay at 1.

### Abuse guard

Signup is open, so an unknown third party can obtain an account and spend Cyte LLM
credits. Mitigations:

- Email confirmation required (a Supabase Auth setting).
- One concurrent run per user.
- Twenty runs per user in any rolling 24-hour window, counted from
  `runs.created_at`, returning 429.

## Error handling

Modelled on the isolation precedent already in `Program.cs:164-172`, where
building the chat payload cannot turn a completed run into an error:

- **Output upload fails after a successful run** → the run stays `done`, with the
  affected artifacts flagged unavailable in `result`. A completed analysis is
  never downgraded to `error` by a transfer problem.
- **Input upload fails** → the run is `error` before any work starts; the user
  retries the upload.
- **Expired session** → supabase-js refreshes silently; only a hard refresh
  failure bounces to the login screen.
- **Another user's run id** → **404, not 403.** A 403 confirms the id exists.
- **Supabase unconfigured at boot** → the app refuses to start, printing a message
  in the shape of the existing `CyteLlm` one (`Program.cs:23-26`) naming the
  missing variables. The LLM warning degrades to "no AI analysis" because the rest
  of the app still works; without Supabase nothing works, so this one is fatal
  rather than starting an app that 500s on every request.
- **Quota exceeded** → 429 with plain English.

## Frontend

`app.js` gains an auth gate rendered before the app: email/password login and
signup, with `supabase-js` from CDN owning the session and its refresh. Every
`fetch` routes through one `api()` helper that injects the bearer token and
bounces to login on 401.

Step 1's four text inputs become four folder pickers
(`<input type="file" webkitdirectory>`), each showing file count and total size
with a client-side size guard. The `hr_paths` localStorage restore
(`app.js:60-68`) is removed — it cannot work with file pickers, and run history
replaces it with something strictly better.

A run-history list shows the caller's past runs; selecting one reopens its
summaries, downloads, dashboard, and chat. Plus a sign-out control.

`index.html:24-27`'s copy about pasting paths and "nothing is copied" is rewritten
— it will be actively wrong.

## Configuration

Render environment variables, alongside the existing `CyteLlm__*` pair:

| Variable | Secret | Used by |
|---|---|---|
| `SUPABASE_URL` | no | server + served to browser |
| `SUPABASE_ANON_KEY` | no | served to browser |
| `SUPABASE_SERVICE_ROLE_KEY` | **yes** | server only, never leaves it |

Environment variables sidestep the user-secrets gap recorded in
`docs/superpowers/2026-07-29-cyte-llm-follow-ups.md` — the web host only registers
user secrets in Development, but `CyteLlm__ClientId`-style environment variables
work in any environment, and Render supplies all configuration that way.

## Testing

`HazardRecon.Core` is untouched, so every existing test stays green. That is the
main structural safety property of this design.

New abstractions mirror the existing `ILlmClient` pattern so web logic is testable
with no live Supabase:

- `IRunStore` — create run, update status, append log, load history, load one run,
  persist chat.
- `IFileStore` — upload, sign, delete by prefix.

Fakes for both, in the shape of the existing `FakeLlmClient`.

New tests:

| Area | Case |
|---|---|
| Path safety | `..` segments, absolute paths, and drive letters in `webkitRelativePath` are rejected |
| Ownership | user B requesting user A's run, job, chat, and output download all get 404 |
| JWT | expired, wrong issuer, wrong signature, and absent tokens are all rejected |
| Quota | 21st run in a day returns 429; a second concurrent run is refused |
| Lifecycle | startup marks orphaned `running` rows as `interrupted` |
| Upload limits | 5 sets, an oversized set, and too many files are all refused server-side |
| Retention | the purge deletes input objects and rows, stamps `inputs_purged_at`, and leaves outputs |
| Storage failure | an output upload failure leaves the run `done` with artifacts flagged |

`tests/client/app.harness.mjs` gains scenarios for the auth gate and history
rendering, in its existing style. A live smoke test against a real Supabase
project mirrors `tests/client/cyte-smoke.mjs`.

## Suggested build order

Each step leaves the app working, so the branch is never in a half-migrated state
that cannot be demonstrated:

1. **Schema + storage bucket.** SQL migration checked into the repo. Nothing
   consumes it yet.
2. **`IRunStore` / `IFileStore` + fakes**, with the Supabase REST implementations
   behind them. Testable in isolation.
3. **Auth.** JWT validation, `GET /api/config`, the login gate, the `api()` helper.
   Existing endpoints become authenticated but otherwise unchanged — still reading
   local paths. Provable end to end.
4. **Upload.** `POST /api/runs` and the folder pickers replace the path inputs and
   `/api/discover`. This is the step that makes Render viable.
5. **Persistence.** Runs, files, and chat write through to Postgres and Storage;
   downloads move to signed URLs; startup reconciliation lands.
6. **History UI.** The list and the reopen path.
7. **Retention.** The 30-day input purge.

Steps 1–3 are independent of 4–7 and could be reviewed separately.

## Open risks

- **Render's single-instance constraint is a deployment setting, not a code
  guarantee.** Someone scaling to 2 instances gets subtly broken logs and spurious
  `interrupted` runs, with no error to point at it. The startup log should state
  the assumption.
- **Folder upload is not universal.** `webkitdirectory` is non-standard; it works
  in Chrome, Edge, and Safari, and in current Firefox. A browser without it needs a
  fallback, which this design does not include — the UI should detect and say so
  rather than silently offering a broken picker.
- **Open signup remains a cost exposure** even with quotas. Twenty runs a day per
  account times unlimited accounts is unbounded. If this is ever more than a
  handful of known users, switch to domain-restricted or invite-only signup.
