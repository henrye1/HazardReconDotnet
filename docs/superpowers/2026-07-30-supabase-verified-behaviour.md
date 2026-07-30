# Supabase project — behaviour to verify

**Status: partly resolved — no longer blocking.**

The one question that used to gate the auth wiring (does the project serve an
OpenID discovery document?) has been designed out. The app reads
`/auth/v1/.well-known/jwks.json` directly via `JwksRetriever`, which works whether
or not discovery exists, so **probe 2 is now informational only.**

Supabase maintainers describe OIDC discovery as [still being added](https://github.com/orgs/supabase/discussions/38983)
— "so you can point your API's `authority` at your Supabase project" — which is
exactly why `options.Authority` was the wrong choice and was removed.

The remaining rows are still worth filling in, because rows 4–6 pin assumptions
the code makes that no offline test can prove.

This mirrors the "Verified gateway behaviour" table in
`specs/2026-07-29-cyte-llm-model-selection-design.md`: probe first, then design
against what the service actually does.

## Probes

Replace `<ref>` with your project ref, then run each command and record the
result.

| # | Probe | Command | Expected | Actual |
|---|---|---|---|---|
| 1 | JWKS endpoint serves a key set | `curl -s "https://<ref>.supabase.co/auth/v1/.well-known/jwks.json"` | JSON with a `keys` array | |
| 2 | OpenID discovery document exists | `curl -s -o /dev/null -w "%{http_code}\n" "https://<ref>.supabase.co/auth/v1/.well-known/openid-configuration"` | either — informational only, the code no longer depends on it | |
| 3 | Signing algorithm | from probe 1, read `keys[0].alg` and `keys[0].kty` | `ES256`/`EC` or `RS256`/`RSA`, **not** a legacy HS256 shared secret | |
| 4 | Issuer claim on a real token | sign up a test user, decode the `access_token` payload, read `iss` | `https://<ref>.supabase.co/auth/v1` | |
| 5 | Audience claim on a real token | same token, read `aud` | `authenticated` | |
| 6 | Subject claim is a uuid | same token, read `sub` | a uuid matching the row in `auth.users` | |

To decode a token payload without a browser:

```bash
TOKEN='<paste access_token>'
echo "$TOKEN" | cut -d. -f2 | tr '_-' '/+' | base64 -d 2>/dev/null
```

## Why rows 4–6 matter

`SupabaseJwt.BuildValidationParameters` pins the issuer to
`{Url}/auth/v1` and the audience to `authenticated`, and
`SupabaseJwt.UserId` parses `sub` as a `Guid`. If any of those three differ on
your project, tokens are rejected at the door and every API call 401s.
`SupabaseJwtTests` proves the validation logic is correct against those
assumptions — it cannot prove the assumptions match your project.

## How keys are resolved

`Program.cs` points a `ConfigurationManager<OpenIdConnectConfiguration>` straight
at `/auth/v1/.well-known/jwks.json` through `JwksRetriever`. No discovery document
is involved, and the manager refreshes on its own schedule, so key rotation is
handled — `JwksRetrieverTests.TestEveryKeyInTheSetIsReturned` pins that both the
outgoing and incoming key survive a rotation, since dropping either would
silently invalidate half the live sessions.

## Schema application

Record these once the migration has been applied:

| Check | Query | Expected | Actual |
|---|---|---|---|
| Tables exist with RLS on | `select tablename, rowsecurity from pg_tables where schemaname='public' and tablename in ('runs','run_files','chat_messages');` | 3 rows, `rowsecurity` true | |
| Policies created | `select count(*) from pg_policies where schemaname='public';` | 3 | |
| Bucket is private | Storage → `runs` bucket | exists, Public off | |
| Email confirmation on | Authentication → Providers → Email | Confirm email ON | |
