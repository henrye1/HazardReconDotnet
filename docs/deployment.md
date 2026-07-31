# Deploying the web app to Render

The web app is multi-user and requires a Supabase project. The CLI is unaffected
by everything here — it stays a local tool with no authentication.

## The image

`render.yaml` sets `runtime: docker`, so Render builds the root `Dockerfile`.
It is a two-stage build: the SDK image restores and publishes, and only the
published output is copied into the smaller ASP.NET runtime image.

Three things about it are deliberate:

- **Only the web app and the engine are built.** The CLI is a local tool and the
  test project is not needed at runtime, so `.dockerignore` keeps both out of the
  build context along with `bin/`, `obj/`, `docs/` and `supabase/`. Excluding
  `bin/` is not only about size: a host Debug build copied over the container's
  output would be the wrong runtime, and its presence busts the restore cache on
  every source edit.
- **It runs as the image's non-root user.** A run writes under `/app/runs` before
  the artifacts reach object storage, so that directory is created and handed to
  that user *before* the `USER` switch. Without it the first run fails on a
  path it cannot write.
- **`appsettings.Development.json` is excluded.** `dotnet publish` copies it by
  default. It holds only logging levels today, but an image is the wrong place
  for anything environment-specific to arrive by accident.

The app binds whatever `HOST` and `PORT` Render supplies rather than hard-coding a
URL, which is why `EXPOSE` is documentation only.

## Health check

`healthCheckPath: /health` is declared, so Render gates a deploy on the app
answering rather than on the container merely starting.

### Two startup warnings that are expected

The container logs both of these on every boot. Neither is a problem *for this
app*, and the reason matters if the app ever grows:

    Storing keys in a directory '/home/app/.aspnet/DataProtection-Keys'
    that may not be persisted outside of the container.

Nothing here uses Data Protection. Authentication is JWT bearer, and the
`/runs`-scoped download cookie carries the Supabase token itself, validated
against the JWKS — there is no antiforgery, no cookie authentication and no
session state. The key ring is created by the framework and never read.

That changes the moment anything Data-Protection-backed is added — antiforgery
tokens, `TempData`, cookie auth. Those would break on every redeploy, silently,
because the keys are regenerated. Persist the key ring before adding any of them.

    Overriding HTTP_PORTS '8080' ... Binding to values defined by URLS
    instead 'http://0.0.0.0:10000'

Expected, and confirmation that `HOST`/`PORT` are being honoured: the base image
defaults to 8080 and the app's own `UseUrls` wins.

## Verified locally

The image has been built and run end to end (`docker build -t hazard-recon:local .`):

- 355 MB, runs as the base image's non-root `app` user (uid 1654)
- `/app/runs` exists and is writable by that user
- `appsettings.Development.json` is absent; no CLI or test assemblies are present
- with no Supabase configuration it refuses to start, exit 1, naming the missing keys
- with configuration it serves `/health`, `/api/config`, `/` and `/app.js`
- `/api/config` does not contain the service-role key
- `/api/runs` and `/api/discover` both answer 401 unauthenticated

## Environment variables

Render supplies all five as environment variables. `render.yaml` marks the
secrets `sync: false`, so Render prompts for their values instead of storing
them in the repository.

| Variable | Where it comes from | Secret |
|---|---|---|
| `Supabase__Url` | Supabase → Project Settings → Data API → Project URL | no |
| `Supabase__AnonKey` | Supabase → Project Settings → API Keys → `anon` / publishable | no — it is served to the browser by design |
| `Supabase__ServiceRoleKey` | Supabase → Project Settings → API Keys → `service_role` | **yes** |
| `CyteLlm__ClientId` | Cyte gateway credentials | **yes** |
| `CyteLlm__ClientSecret` | Cyte gateway credentials | **yes** |

### The service-role key

`Supabase__ServiceRoleKey` bypasses row-level security completely. Anyone
holding it can read and write every user's data. It must never be sent to the
browser, written to a log, or committed.

Two things in the code exist to keep that true, and both have tests:

- `GET /api/config` returns only the URL and the anon key
  (`AuthEndpointTests.TestConfigServesTheAnonKeyButNeverTheServiceRoleKey`).
- `SupabaseRestClient` builds failure messages from the response body alone, never
  the request headers
  (`SupabaseRestClientTests.TestTheServiceRoleKeyIsNeverInTheExceptionMessage`).

### Why environment variables rather than user secrets

`docs/superpowers/2026-07-29-cyte-llm-follow-ups.md` records that the web host
only registers the user-secrets provider when `ASPNETCORE_ENVIRONMENT` is
`Development`, so a published app started in Production silently found no
`CyteLlm` credentials. Environment variables are read in every environment, which
is what Render supplies, so that gap does not apply to this deployment. The app
now also refuses to start at all if the Supabase values are missing, rather than
starting and failing on every request.

## Single instance — required

Render's scaling setting must stay at **one instance**.

While a run is in flight its progress log lives in an in-process cache; Postgres
is the durable record, written on completion. With two instances, a poll routed
to the wrong one sees no log, and each instance's startup would mark the other's
live runs as `interrupted`.

Nothing in the code can detect this, which is why the startup banner states the
assumption and `render.yaml` pins `numInstances: 1`.

## Disk, and the upload limit

Render's filesystem is ephemeral, which the app already assumes: a download whose
file is not on disk is served from object storage instead
(`GET /runs/{rid}/output/{filename}` falls through to a signed URL). A redeploy
therefore loses no artifact. It does abandon any run in flight — those rows are
marked `interrupted` on the next startup.

What ephemeral disk does constrain is uploading. `ReadFormAsync` buffers the whole
request before `UploadReceiver` streams it into the run folder, so a folder needs
roughly **twice its size** in scratch space while it lands.

`Uploads:MaxBytesPerSet` defaults to 512 MB, and with four sets that makes
Kestrel's ceiling about 2 GB. That is a cap, not a reservation — a real debug
folder is nearer 160 MB, so a four-set run needs about 1.3 GB of scratch rather
than 4 GB. Still, if deploys start failing on disk during upload, lower it:

    Uploads__MaxBytesPerSet=134217728    # 128 MB per set

The browser reads that limit from `/api/config` rather than hard-coding its own,
so the folder picker enforces whatever the server is configured with and no client
change is needed.

## Before the first deploy

1. Apply `supabase/migrations/20260730000000_auth_and_runs.sql` to the project.
2. Create a **private** storage bucket named `runs`, with no storage policies.
3. Turn on **Confirm email** under Authentication → Providers → Email.
4. Work through `docs/superpowers/2026-07-30-supabase-verified-behaviour.md` and
   record the results. Nothing there blocks the deploy — the app reads the JWKS
   directly and needs no discovery document — but rows 4–6 pin the issuer,
   audience, and `sub` claim the code assumes, and no offline test can prove
   those match your project.

## Verifying a deploy

`GET /health` is unauthenticated and returns `{"ok":true,...}`.

Everything else requires a bearer token, so a browser hitting the root URL should
land on the sign-in gate rather than the reconciliation form.
