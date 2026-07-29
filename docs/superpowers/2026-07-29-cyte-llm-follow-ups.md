# Cyte LLM gateway — known follow-ups

Recorded 2026-07-29, at the close of the `feat/cyte-llm` work.

Everything here was found during the task loop or the final whole-branch review,
triaged as **ship-it**, and deliberately not fixed. The blocking findings were
fixed in `358d14f`. This file exists so the ship-it list survives — it was held in
a git-ignored scratch directory that has since been deleted.

Nothing below blocks merge. Each line says what it is and why it was left.

## Correctness / behaviour

- **The two hosts read user secrets under different rules.** `Web/Program.cs` binds
  from `builder.Configuration`, whose user-secrets provider `WebApplication.CreateBuilder`
  adds **only when `ASPNETCORE_ENVIRONMENT == Development`**. The CLI calls
  `.AddUserSecrets<Program>()` unconditionally. So a published web app started without
  `ASPNETCORE_ENVIRONMENT` (defaulting to Production) reports
  `CyteLlm:ClientId / ClientSecret not set` and silently has no AI, while the CLI on the
  same machine with the same shared store works.
  It degrades safely and `CyteLlm__ClientId` environment variables still work, so this is
  an expectation gap rather than a break — **but the spec's claim that "one `UserSecretsId`
  is shared by Web and Cli so both read the same store" is only true in Development.**
  Fix by adding an explicit `.AddUserSecrets<Program>()` to the web host, or by documenting
  that production deployments supply configuration by environment variable.

- **`CyteLlmClient` is never disposed at process shutdown.** One instance per process,
  `IDisposable`, owning an `HttpClient` and a `SemaphoreSlim`. Harmless because the process
  exits and the OS reclaims the handles. Add an `ApplicationStopping` hook only if a second
  client instance ever appears.

- **`--model --no-analysis` consumes the following flag as a literal model fragment.** The
  argument parser only checks "is there a next token", not "does it look like a value".
  Identical to the pre-existing behaviour of `--root` and `--outdir`; fixing only `--model`
  would make the parser inconsistent. Fix all three together.

- **The engine's `"no model selected - skipping AI analysis"` warning is unreachable from
  either host**, since both pass `analyze: analyst != null`. **Decision: keep the branch.**
  `Run` is public and `analyze`/`analyst` are independent parameters, so it is a real guard
  for a direct caller, and `EngineAnalysisTests` pins it. Collapsing to one parameter would
  force edits to the 20 pre-existing `analyze: false` tests for no gain.

- **The browser's empty-model-list copy is misleading.** `[]` is a valid array, so the select
  stays enabled with only "Skip AI analysis" while the note still reads "Analysis adds
  roughly 25 seconds to a run." Behaviour is correct (the run proceeds without analysis);
  only the text is wrong.

## Test coverage

- **No concurrency test exercises the `SemaphoreSlim` single-flight guard** in
  `CyteLlmClient`. The guarded state is only read and written inside the lock, so a
  deterministic test here is fiddly and low-yield.

- **`TestIsConfiguredRequiresEveryValue` blanks only 2 of 5 fields** individually. The five
  clauses are identical `&&` terms, so blanking two proves the pattern.

- **No chat-specific 401-retry test.** The retry lives in the shared `SendAsync` and is
  covered twice via `ListModelsAsync`; the chat-specific delta (URL, body, 404) is covered
  by `TestChatPostsToTheModelSpecificUrl` and `TestUnknownModelIdSurfacesAs404`.

- **`ListModelsAsync`'s non-array early return is untested** — `TestEmptyArrayYieldsNoModels`
  covers `[]` only, not a malformed non-array body. Low risk: every caller wraps in
  `catch (Exception)`.

- **A real user click firing the `change` event → `localStorage` write is unverified.**
  A headless render confirmed the select and its options are built correctly, and harness
  `scenarioH` covers restore-and-fallback in both directions, so the only uncovered link is
  the one-line listener. Needs a human at a browser.

## Tidiness

- **Test helper duplication across three `Llm` test classes** — `Options()`, the token JSON,
  and `IsToken` are repeated in `CyteLlmClientTokenTests`, `CyteLlmClientModelsTests` and
  `CyteLlmClientChatTests`. Extract an `LlmTestHelpers` the next time a fourth client test
  class appears.

- **Unused members on `FakeLlmClient`** (`Models`, `ChatCalls`) and an **unused parameterless
  `LlmMessage()` constructor**. Note that `FakeLlmClient.ListModelsAsync` is *not* dead — it
  is a mandatory `ILlmClient` member.

- **`app.js` section comment numbering drifted.** `/* step 2: model */` was inserted before
  the unlabelled discover section while `/* step 3: run */` follows, so the comments no
  longer track the UI's step numbering. Comment text only.

- **`src/HazardRecon.Core/Class1.cs`** is a leftover project-template class, unrelated to this
  work and never referenced. Safe to delete whenever someone is in that folder.
