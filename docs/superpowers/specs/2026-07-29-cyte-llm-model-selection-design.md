# Cyte LLM gateway + user-selected model

**Date:** 2026-07-29
**Status:** approved, not yet implemented

## Problem

The AI analysis path calls the Anthropic API directly, with the model hard-coded
(`claude-opus-5`) and the key read inline from an `ANTHROPIC_API_KEY` environment
variable in two places (`AiAnalysisService.cs:102`, `ChatService.cs:39`). Users
cannot choose a model, and neither service has a test, because neither can be
constructed without reaching the network.

We are moving to the Cyte LLM gateway, which fronts several providers behind one
API and lets the user pick per run.

## Goals

- Reach models through the Cyte gateway: client-credentials token, list models,
  chat.
- Let the user pick a model in the web UI before a run; that run's analysis uses it.
- Make the analysis and chat paths testable without network or credentials.
- Keep credentials out of the repository.

## Non-goals

- **Streaming responses.** The gateway's chat endpoint is request/response.
- **Provider-specific request shaping.** The gateway normalises this; we send one
  message shape to every model.
- **Honouring `defaultParameters`.** The gateway applies its own defaults; we do
  not send `temperature` or `maxTokens`. (Verified: extra params are accepted and
  ignored without error, so this stays open as a later addition.)
- **Full parity with the Python `chat.py` tool loop.** The Python original runs a
  pandas sandbox over the run's dataframes and masks account numbers before they
  leave the machine. That is substantially more work than the current stub
  warrants. See "Chat scope" below for what we build instead.
- **Anthropic as a fallback.** The direct Anthropic path is removed entirely.

## Verified gateway behaviour

Probed against QA on 2026-07-29 before designing, because several of these
determine the design:

| Behaviour | Result |
|---|---|
| `POST /oauth/token` (client_credentials) | 200; `access_token`, `expires_in: 86400`, `token_type: Bearer` |
| `GET /api/llm/models` | 200; 2 models — Gemini 2.5 Pro (`provider: 1`), Azure GPT-4o (`provider: 0`) |
| `system` role in chat `messages` | **Honoured.** A system instruction to reply only "BANANA" was obeyed. |
| Extra body params (`temperature`, `maxTokens`) | Accepted, no error |
| No token | 401 |
| Unknown model id | 404 |
| Analysis-sized request, Gemini 2.5 Pro | 26.7s, 611 words, 6 `##` sections, 316 in / 941 out tokens |
| Analysis-sized request, Azure GPT-4o | 22.9s, 200 OK |

The system-role result is load-bearing: the existing analysis system prompt can be
carried over verbatim as a `system` message rather than being folded into the user
turn.

Latency of ~23–27s is faster than the 38–52s observed on the direct Anthropic
calls, but still long enough that the picker offers a "skip" option.

## Architecture

New folder `src/HazardRecon.Core/Llm/`:

**`CyteLlmOptions`** — `TokenUrl`, `Audience`, `ApiBaseUrl`, `ClientId`,
`ClientSecret`. A plain POCO; Core takes no dependency on
`Microsoft.Extensions.Configuration`.

**`LlmModel`** — `Id` (string, GUID form), `Provider` (int), `FriendlyName`,
`ModelName`. `defaultParameters` is deliberately not surfaced (see non-goals).

**`LlmMessage`** — `Role`, `Content`. **`LlmChatResult`** — `Content`,
`InputTokens`, `OutputTokens`.

**`ILlmClient`**

```csharp
Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default);
Task<LlmChatResult> ChatAsync(string modelId, IReadOnlyList<LlmMessage> messages,
                              CancellationToken ct = default);
```

**`CyteLlmClient : ILlmClient`** — owns one reused `HttpClient` with an explicit
120s timeout (the current Anthropic call sets none and inherits 100s).

Token handling:
- Cached in memory as `(token, expiresAtUtc)`, computed from `expires_in`.
- Refreshed when `now >= expiresAtUtc - 5 minutes`.
- Guarded by a `SemaphoreSlim` so concurrent callers fetch once.
- On a 401 from `models` or `chat`: discard the cached token, fetch once, retry the
  call once. A second 401 surfaces as a failure — no retry loop.
- Constructor takes `Func<DateTime> utcNow` defaulting to `() => DateTime.UtcNow`,
  so expiry is testable without waiting 24 hours.

URLs are built as `{ApiBaseUrl}/llm/models` and
`{ApiBaseUrl}/llm/models/{modelId}/chat`, with `ApiBaseUrl` =
`https://coreapi-qa.cyte.co.za/api` (no trailing slash). Note the `Audience` value
does carry a trailing slash and is passed through unchanged.

### Changed components

**`AiAnalysisService`** — becomes an instance class taking `ILlmClient` and a model
id. `BuildAnalysisPayload` is unchanged. The existing system prompt is carried over
verbatim, now sent as a `system` message with the JSON payload as the `user`
message. Returns `null` on any failure, logging a warning — matching today's
behaviour when the key is missing, so a gateway outage never fails a run.

`ILlmClient` is async, but `ReconciliationEngine.Run` is synchronous and already
executes inside a `Task.Run` on the web path. The single call site blocks with
`.GetAwaiter().GetResult()`, as the current code does. This is safe here (no
synchronisation context) and avoids making the whole engine async for one call.

**`ChatService`** — replaces the canned response with a real call. Keeps the
existing contract: `IsError` + message, surfaced as HTTP 503.

**`ReconciliationEngine.Run`** — gains an optional `AiAnalysisService? analyst`.
`analyze: true` with a null analyst logs `"no model selected - skipping AI
analysis"` and continues, exactly as a missing key does today.

**`Web/Program.cs`**
- Binds `CyteLlmOptions` from the `CyteLlm` configuration section; registers one
  `CyteLlmClient` singleton.
- `GET /api/models` — returns `[{ id, provider, friendlyName, modelName }]`. On
  failure returns 503 with a readable reason.
- `POST /api/run` — body gains `model_id`. Absent or empty means no analysis for
  that run. The id is **not** pre-validated against the model list; an unknown id
  reaches the gateway, returns 404, and surfaces as a skipped analysis with a
  warning in the run log. This keeps `/api/run` from depending on a live gateway
  call just to start a reconciliation.
- `JobState` gains `ModelId`, so `/api/chat` reuses the model the run was made with
  rather than asking again.
- `POST /api/chat` — 503 `"No model was selected for this run."` when `ModelId` is
  null.

**`wwwroot/index.html` + `app.js`** — a `<select id="model">` on the run card,
populated from `/api/models` on page load. First option is
`Skip AI analysis` with an empty value; then one option per model, labelled
`friendlyName`, valued `id`. Selection persists in `localStorage` under `hr_model`,
like the existing `hr_paths`; if the remembered id is no longer in the list it is
discarded and the selection falls back to `Skip AI analysis`. If `/api/models`
fails the select is disabled with a single option carrying the reason, and runs
still work — without analysis.

**`Cli/Program.cs`** — `--model <id or friendly-name fragment>`. If omitted while
analysis is enabled, the first model returned is used and logged. Without this,
removing the Anthropic path would silently disable CLI analysis. Matching is
case-insensitive against both `id` and `friendlyName`; the first match in the order
the gateway returns wins, so an ambiguous fragment is not an error. An unmatched
fragment *is* an error, and prints the available models.

## Chat scope

The chat sends the same aggregate payload the analysis uses, plus the user's
question, and returns the model's answer. Consequences, stated deliberately:

- **No account-level data leaves the machine**, so the masking helpers already in
  `ChatService` are not needed on this path. That is simpler *and* safer than
  masking account numbers, which is what the Python version has to do because it
  ships row-level frames to the model.
- The model therefore cannot answer questions about individual accounts. It can
  answer questions about totals, rates, and the reconciliation outcome.

Reaching Python's per-account capability means porting its pandas tool loop and its
masking contract, which is a separate piece of work.

## Configuration and secrets

`appsettings.json` — non-secret, committed:

```json
"CyteLlm": {
  "TokenUrl": "https://auth-qa.cyte.co.za/oauth/token",
  "Audience": "https://coreapi-qa.cyte.co.za/api/",
  "ApiBaseUrl": "https://coreapi-qa.cyte.co.za/api"
}
```

User secrets — the two actual secrets, never committed:

```
dotnet user-secrets set "CyteLlm:ClientId"     "<supplied separately>"
dotnet user-secrets set "CyteLlm:ClientSecret" "<supplied separately>"
```

One `UserSecretsId` is shared by `HazardRecon.Web` and `HazardRecon.Cli` so both
read the same store. `ANTHROPIC_API_KEY` is no longer read anywhere.

The QA client secret was pasted into a chat transcript during design. User secrets
keep it out of the repository, but it should be rotated at some point regardless.

## Error handling

Analysis is optional today and stays optional. Token failure, 401, an empty model
list, an unreachable gateway, or a malformed response all log a warning and let the
run finish with workbook, CSVs and dashboard intact.

| Failure | Behaviour |
|---|---|
| Token call fails | `/api/models` → 503 with reason; picker disabled; runs proceed without analysis |
| Token expired mid-run | Refreshed transparently; one retry on 401 |
| Unknown model id | Gateway 404 → analysis skipped with a warning |
| Chat called with no model | 503 `"No model was selected for this run."` |
| Chat call fails | 503 with the reason, existing UI error bubble |

## Testing

Unit tests against a `FakeHttpMessageHandler` that records requests and returns
canned responses. No network, no credentials.

`CyteLlmClient`:
1. Token fetched once when two calls occur inside the validity window.
2. Token refetched after expiry, via the injected clock.
3. A 401 triggers exactly one token refresh and one retry, then succeeds.
4. Two consecutive 401s surface a failure rather than looping.
5. `ListModelsAsync` parses the documented payload into two models.
6. `ChatAsync` posts to `/llm/models/{id}/chat` with `system` then `user`, in order.
7. `ChatAsync` parses `content` and both usage counts.

`AiAnalysisService`:
8. Returns the model's markdown on success.
9. Returns `null` and logs a warning when the client throws.

`ReconciliationEngine`:
10. `analyze: true` with a null analyst completes the run and logs the skip.

`ChatService`:
11. Returns an error response when no model id is configured.

The existing 20 tests run with `analyze: false` and must stay green.

A manual smoke script under `tests/client/` hits the real QA endpoints — kept out
of the unit suite, like the existing `app.harness.mjs` and `dashboard-heat.mjs`.

## Notes

This repository is not under git, so this spec cannot be committed. If it is
initialised later, this file should go in with the implementation.
