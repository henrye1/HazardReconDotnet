# Deploying the web app to Render

The web app is multi-user and requires a Supabase project. The CLI is unaffected
by everything here — it stays a local tool with no authentication.

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
