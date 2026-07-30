# Supabase project — behaviour to verify

**Status: NOT YET VERIFIED.** No Supabase project existed when the code was
written, so every row below is unfilled. The auth code currently assumes the
**Authority** path (row 2 answering "yes"). Fill this in before trusting the
deployment, and if row 2 turns out to be "no", switch to the JWKS fallback —
see "If the discovery document is absent" at the bottom.

This mirrors the "Verified gateway behaviour" table in
`specs/2026-07-29-cyte-llm-model-selection-design.md`: probe first, then design
against what the service actually does.

## Probes

Replace `<ref>` with your project ref, then run each command and record the
result.

| # | Probe | Command | Expected | Actual |
|---|---|---|---|---|
| 1 | JWKS endpoint serves a key set | `curl -s "https://<ref>.supabase.co/auth/v1/.well-known/jwks.json"` | JSON with a `keys` array | |
| 2 | OpenID discovery document exists | `curl -s -o /dev/null -w "%{http_code}\n" "https://<ref>.supabase.co/auth/v1/.well-known/openid-configuration"` | `200` or `404` — **this decides the auth wiring** | |
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

## If the discovery document is absent

If probe 2 returns 404, the `options.Authority` line in `Program.cs` cannot
resolve keys. Replace it with the explicit JWKS configuration manager and add
`JwksRetriever` — both are written out in full in
`plans/2026-07-30-supabase-auth-foundation.md`, Task 6 step 6.

## Schema application

Record these once the migration has been applied:

| Check | Query | Expected | Actual |
|---|---|---|---|
| Tables exist with RLS on | `select tablename, rowsecurity from pg_tables where schemaname='public' and tablename in ('runs','run_files','chat_messages');` | 3 rows, `rowsecurity` true | |
| Policies created | `select count(*) from pg_policies where schemaname='public';` | 3 | |
| Bucket is private | Storage → `runs` bucket | exists, Public off | |
| Email confirmation on | Authentication → Providers → Email | Confirm email ON | |
