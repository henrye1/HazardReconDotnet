# Supabase Auth Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the hazard-rate web app behind a Supabase email/password login, and build the tested Postgres/Storage data layer that run persistence will sit on.

**Architecture:** Supabase as BaaS, .NET as the only trusted backend. The browser authenticates with `supabase-js` and sends a bearer token; .NET validates it against Supabase's JWKS and does all data access itself with the service-role key. Row-level security is deny-by-default defense-in-depth, not the enforcement mechanism.

**Tech Stack:** .NET 10 minimal API, xunit, vanilla JS + `supabase-js` from CDN, Supabase (Postgres + Storage + Auth).

**Spec:** `docs/superpowers/specs/2026-07-30-supabase-auth-and-run-persistence-design.md`

**Scope:** This plan covers build-order steps 1–3 of the spec (schema, store abstractions, auth). Upload, run persistence, history UI, and retention are steps 4–7 and belong to a second plan, written after this one is executed against real interfaces. At the end of this plan the app requires a login and everything else behaves exactly as it does today — still reading server-local folder paths.

## Global Constraints

- **`HazardRecon.Core` is never modified.** Every existing test must stay green. If a change seems to require touching Core, stop and flag it.
- **`HazardRecon.Cli` is never modified.** It stays a local, unauthenticated tool.
- Target framework is `net10.0`, `Nullable` and `ImplicitUsings` enabled, matching every existing project.
- Test naming follows the existing convention: `TestSomethingDoesSomething`, xunit `[Fact]`, `Assert.*`. See `tests/HazardRecon.Tests/Llm/CyteLlmClientModelsTests.cs`.
- New HTTP clients take an optional `HttpMessageHandler` as their last constructor parameter for testing, exactly like `CyteLlmClient(options, handler)`.
- Reuse `tests/HazardRecon.Tests/Llm/FakeHttpMessageHandler.cs` — do not write a second HTTP fake.
- New types in `HazardRecon.Web` that tests touch must be `public` (the test assembly has no `InternalsVisibleTo`).
- The service-role key is never sent to the browser, never logged, and never appears in a test fixture as a realistic value.
- Configuration comes from environment variables in production (Render). Never commit a key.
- Commit after every task.

---

### Task 1: Supabase project, schema, and storage bucket

Infrastructure. No .NET code. The deliverable is a migration file in the repo and a verified record of how this Supabase project signs tokens — the same "verify before designing against it" practice used for the Cyte gateway.

**Files:**
- Create: `supabase/migrations/20260730000000_auth_and_runs.sql`
- Create: `docs/superpowers/2026-07-30-supabase-verified-behaviour.md`

**Interfaces:**
- Consumes: nothing.
- Produces: tables `public.runs`, `public.run_files`, `public.chat_messages`; private storage bucket `runs`; the recorded values `SUPABASE_URL`, issuer, and JWKS URL used by Task 5.

- [ ] **Step 1: Create the Supabase project**

In the Supabase dashboard, create a project. Under **Authentication → Providers → Email**, enable email provider and turn **Confirm email** ON (the spec's abuse guard). Under **Authentication → Sign In / Providers**, leave signup enabled.

Record the project URL (`https://<ref>.supabase.co`) — it is `SUPABASE_URL`.

- [ ] **Step 2: Confirm the project uses asymmetric JWT signing keys**

In **Project Settings → JWT Keys**, confirm the project uses JWT signing keys (asymmetric, ECC or RSA) rather than the legacy shared HS256 secret. If it shows a legacy secret, migrate to signing keys — this plan validates tokens via JWKS and has no HS256 path.

- [ ] **Step 3: Verify the auth endpoints and record what you find**

Run these against your project and record the actual results:

```bash
curl -s "https://<ref>.supabase.co/auth/v1/.well-known/jwks.json" | head -c 400
curl -s -o /dev/null -w "%{http_code}\n" "https://<ref>.supabase.co/auth/v1/.well-known/openid-configuration"
```

Write `docs/superpowers/2026-07-30-supabase-verified-behaviour.md` with a table recording: whether the JWKS endpoint returns a key set and which `alg`/`kty` it advertises, whether the OpenID configuration document exists (HTTP status), and the exact `iss` claim of a real access token (get one by signing up a test user and decoding the returned `access_token` payload at jwt.io or with `base64 -d`).

**This determines Task 5.** If the OpenID configuration document returns 200, Task 5 uses the `Authority` path. If it 404s, Task 5 uses the explicit JWKS resolver. Both are written out in Task 5 — pick the one your recorded result supports.

- [ ] **Step 4: Write the schema migration**

Create `supabase/migrations/20260730000000_auth_and_runs.sql`:

```sql
-- Runs, their files, and their chat history. All strictly per-user.
create table public.runs (
  id                uuid primary key default gen_random_uuid(),
  user_id           uuid not null references auth.users(id) on delete cascade,
  status            text not null default 'ready'
                      check (status in ('ready','running','done','error','interrupted')),
  model_id          text,
  set_labels        jsonb not null default '[]'::jsonb,
  log               jsonb not null default '[]'::jsonb,
  result            jsonb,
  analysis_payload  jsonb,
  error             text,
  created_at        timestamptz not null default now(),
  started_at        timestamptz,
  finished_at       timestamptz,
  inputs_purged_at  timestamptz
);

create index runs_user_created_idx on public.runs (user_id, created_at desc);

create table public.run_files (
  id             uuid primary key default gen_random_uuid(),
  run_id         uuid not null references public.runs(id) on delete cascade,
  user_id        uuid not null references auth.users(id) on delete cascade,
  kind           text not null check (kind in ('input','output')),
  set_key        text,
  relative_path  text not null,
  storage_path   text not null,
  size_bytes     bigint not null,
  created_at     timestamptz not null default now()
);

create index run_files_run_idx on public.run_files (run_id);

create table public.chat_messages (
  id            uuid primary key default gen_random_uuid(),
  run_id        uuid not null references public.runs(id) on delete cascade,
  user_id       uuid not null references auth.users(id) on delete cascade,
  role          text not null check (role in ('user','assistant')),
  content       text not null,
  content_html  text,
  created_at    timestamptz not null default now()
);

create index chat_messages_run_created_idx on public.chat_messages (run_id, created_at);

-- Defense in depth only. The server writes as service_role, which bypasses RLS.
-- These policies exist so a leaked anon key reads nothing, not as the primary
-- enforcement mechanism -- that is the server filtering on the token's sub claim.
alter table public.runs enable row level security;
alter table public.run_files enable row level security;
alter table public.chat_messages enable row level security;

create policy "own runs readable" on public.runs
  for select to authenticated using (auth.uid() = user_id);

create policy "own run files readable" on public.run_files
  for select to authenticated using (auth.uid() = user_id);

create policy "own chat readable" on public.chat_messages
  for select to authenticated using (auth.uid() = user_id);
```

Note there are deliberately no insert, update, or delete policies for any role.

- [ ] **Step 5: Apply the migration**

Paste the file into the Supabase SQL editor and run it, or `supabase db push` if the CLI is linked.

- [ ] **Step 6: Create the private storage bucket**

**Storage → New bucket**, name `runs`, **Public: off**. Add no storage policies — only the service-role key should reach it.

- [ ] **Step 7: Verify the schema landed**

Run in the SQL editor:

```sql
select tablename, rowsecurity from pg_tables
where schemaname = 'public' and tablename in ('runs','run_files','chat_messages');
```

Expected: three rows, `rowsecurity` true for all three.

```sql
select count(*) from pg_policies where schemaname = 'public';
```

Expected: 3.

- [ ] **Step 8: Commit**

```bash
git add supabase/migrations/20260730000000_auth_and_runs.sql docs/superpowers/2026-07-30-supabase-verified-behaviour.md
git commit -m "feat: add Supabase schema for runs, files, and chat history"
```

---

### Task 2: SupabaseOptions and fail-fast startup

**Files:**
- Create: `src/HazardRecon.Web/Supabase/SupabaseOptions.cs`
- Create: `tests/HazardRecon.Tests/Web/SupabaseOptionsTests.cs`
- Modify: `tests/HazardRecon.Tests/HazardRecon.Tests.csproj` (add a Web project reference)
- Modify: `src/HazardRecon.Web/Program.cs:19-26` (bind and fail fast)
- Modify: `src/HazardRecon.Web/appsettings.json`

**Interfaces:**
- Consumes: nothing.
- Produces: `public class SupabaseOptions` with `string Url`, `string AnonKey`, `string ServiceRoleKey`, `bool IsConfigured`, and `IReadOnlyList<string> MissingKeys()`.

- [ ] **Step 1: Add the Web project reference to the test project**

In `tests/HazardRecon.Tests/HazardRecon.Tests.csproj`, add to the existing `ItemGroup` holding the Core reference:

```xml
    <ProjectReference Include="..\..\src\HazardRecon.Web\HazardRecon.Web.csproj" />
```

- [ ] **Step 2: Write the failing test**

Create `tests/HazardRecon.Tests/Web/SupabaseOptionsTests.cs`:

```csharp
using HazardRecon.Web.Supabase;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseOptionsTests
{
    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co",
        AnonKey = "anon-key",
        ServiceRoleKey = "service-key"
    };

    [Fact]
    public void TestFullyPopulatedOptionsAreConfigured()
    {
        Assert.True(Options().IsConfigured);
        Assert.Empty(Options().MissingKeys());
    }

    [Fact]
    public void TestBlankUrlIsNotConfigured()
    {
        SupabaseOptions o = Options();
        o.Url = "   ";

        Assert.False(o.IsConfigured);
        Assert.Contains("Supabase:Url", o.MissingKeys());
    }

    [Fact]
    public void TestBlankServiceRoleKeyIsNotConfigured()
    {
        SupabaseOptions o = Options();
        o.ServiceRoleKey = "";

        Assert.False(o.IsConfigured);
        Assert.Contains("Supabase:ServiceRoleKey", o.MissingKeys());
    }

    [Fact]
    public void TestMissingKeysNamesEveryBlankField()
    {
        SupabaseOptions o = new();

        Assert.Equal(
            new[] { "Supabase:Url", "Supabase:AnonKey", "Supabase:ServiceRoleKey" },
            o.MissingKeys());
    }

    [Fact]
    public void TestTrailingSlashIsStrippedFromUrl()
    {
        SupabaseOptions o = Options();
        o.Url = "https://ref.supabase.co/";

        Assert.Equal("https://ref.supabase.co", o.BaseUrl);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter SupabaseOptionsTests`
Expected: FAIL — the namespace `HazardRecon.Web.Supabase` does not exist.

- [ ] **Step 4: Write the implementation**

Create `src/HazardRecon.Web/Supabase/SupabaseOptions.cs`:

```csharp
namespace HazardRecon.Web.Supabase;

/// <summary>
/// Connection settings for the Supabase project. Mirrors the CyteLlmOptions
/// shape so both hosts configure the same way.
/// </summary>
public class SupabaseOptions
{
    public string Url { get; set; } = string.Empty;
    public string AnonKey { get; set; } = string.Empty;
    public string ServiceRoleKey { get; set; } = string.Empty;

    /// <summary>Url without a trailing slash, so callers can concatenate paths.</summary>
    public string BaseUrl => Url.TrimEnd('/');

    public bool IsConfigured => MissingKeys().Count == 0;

    public IReadOnlyList<string> MissingKeys()
    {
        List<string> missing = new();
        if (string.IsNullOrWhiteSpace(Url)) missing.Add("Supabase:Url");
        if (string.IsNullOrWhiteSpace(AnonKey)) missing.Add("Supabase:AnonKey");
        if (string.IsNullOrWhiteSpace(ServiceRoleKey)) missing.Add("Supabase:ServiceRoleKey");
        return missing;
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/HazardRecon.Tests --filter SupabaseOptionsTests`
Expected: PASS, 5 tests.

- [ ] **Step 6: Add the configuration section**

In `src/HazardRecon.Web/appsettings.json`, add a `Supabase` section alongside the existing `CyteLlm` one, with all three values as empty strings. Real values come from environment variables (`Supabase__Url`, `Supabase__AnonKey`, `Supabase__ServiceRoleKey`).

- [ ] **Step 7: Bind and fail fast in Program.cs**

In `src/HazardRecon.Web/Program.cs`, immediately after the existing `CyteLlm` block that ends at line 26, add:

```csharp
SupabaseOptions supabaseOptions = new();
builder.Configuration.GetSection("Supabase").Bind(supabaseOptions);

if (!supabaseOptions.IsConfigured)
{
    // Unlike the LLM, this is fatal. Without Supabase there is no login and no
    // storage, so every request would 500 - fail loudly at boot instead.
    Console.Error.WriteLine(
        " ! Supabase is not configured. Missing: " + string.Join(", ", supabaseOptions.MissingKeys()));
    return 1;
}
```

Add `using HazardRecon.Web.Supabase;` to the usings at the top. Because the file now returns a value, change the final `app.Run();` to `app.Run();` followed by `return 0;`.

- [ ] **Step 8: Verify the app fails fast**

Run: `dotnet run --project src/HazardRecon.Web`
Expected: prints the missing-key message and exits non-zero.

Then run with the values supplied and confirm it starts:

```bash
Supabase__Url=https://ref.supabase.co Supabase__AnonKey=x Supabase__ServiceRoleKey=y \
  dotnet run --project src/HazardRecon.Web
```

Expected: the usual banner, server listening.

- [ ] **Step 9: Run the whole suite**

Run: `dotnet test`
Expected: PASS — every pre-existing test still green.

- [ ] **Step 10: Commit**

```bash
git add src/HazardRecon.Web tests/HazardRecon.Tests
git commit -m "feat: add Supabase options with fail-fast startup"
```

---

### Task 3: SupabaseRestClient

The shared HTTP layer for PostgREST and Storage: service-role headers on every call, non-2xx mapped to a typed exception.

**Files:**
- Create: `src/HazardRecon.Web/Supabase/SupabaseException.cs`
- Create: `src/HazardRecon.Web/Supabase/SupabaseRestClient.cs`
- Create: `tests/HazardRecon.Tests/Web/SupabaseRestClientTests.cs`

**Interfaces:**
- Consumes: `SupabaseOptions` from Task 2.
- Produces: `public class SupabaseRestClient` with constructor `(SupabaseOptions options, HttpMessageHandler? handler = null)` and method
  `Task<string> SendAsync(HttpMethod method, string path, HttpContent? content = null, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)`
  where `path` is relative to the project URL (e.g. `/rest/v1/runs`). Returns the response body. Throws `SupabaseException` on non-2xx.
  Also `public class SupabaseException : Exception` with `int StatusCode`.

- [ ] **Step 1: Write the failing test**

Create `tests/HazardRecon.Tests/Web/SupabaseRestClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using HazardRecon.Tests.Llm;
using HazardRecon.Web.Supabase;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseRestClientTests
{
    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co",
        AnonKey = "anon-key",
        ServiceRoleKey = "service-key"
    };

    [Fact]
    public async Task TestEveryRequestCarriesTheServiceRoleKey()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));
        SupabaseRestClient client = new(Options(), handler);

        await client.SendAsync(HttpMethod.Get, "/rest/v1/runs");

        Assert.Single(handler.Requests);
        Assert.Equal("https://ref.supabase.co/rest/v1/runs", handler.Requests[0].Url);
    }

    [Fact]
    public async Task TestTheBodyIsReturnedVerbatim()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, """[{"id":"abc"}]"""));
        SupabaseRestClient client = new(Options(), handler);

        string body = await client.SendAsync(HttpMethod.Get, "/rest/v1/runs");

        Assert.Equal("""[{"id":"abc"}]""", body);
    }

    [Fact]
    public async Task TestExtraHeadersAreSent()
    {
        string? prefer = null;
        FakeHttpMessageHandler handler = new((req, _) =>
        {
            prefer = req.Headers.TryGetValues("Prefer", out var v) ? string.Join(",", v) : null;
            return (HttpStatusCode.OK, "[]");
        });
        SupabaseRestClient client = new(Options(), handler);

        await client.SendAsync(HttpMethod.Post, "/rest/v1/runs",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            new Dictionary<string, string> { ["Prefer"] = "return=representation" });

        Assert.Equal("return=representation", prefer);
    }

    [Fact]
    public async Task TestNon2xxThrowsWithTheStatusAndBody()
    {
        FakeHttpMessageHandler handler = new((_, _) =>
            (HttpStatusCode.BadRequest, """{"message":"bad column"}"""));
        SupabaseRestClient client = new(Options(), handler);

        SupabaseException ex = await Assert.ThrowsAsync<SupabaseException>(
            () => client.SendAsync(HttpMethod.Get, "/rest/v1/runs"));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("bad column", ex.Message);
    }

    [Fact]
    public async Task TestTheServiceRoleKeyIsNeverInTheExceptionMessage()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.Forbidden, "denied"));
        SupabaseRestClient client = new(Options(), handler);

        SupabaseException ex = await Assert.ThrowsAsync<SupabaseException>(
            () => client.SendAsync(HttpMethod.Get, "/rest/v1/runs"));

        Assert.DoesNotContain("service-key", ex.Message);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter SupabaseRestClientTests`
Expected: FAIL — `SupabaseRestClient` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/HazardRecon.Web/Supabase/SupabaseException.cs`:

```csharp
namespace HazardRecon.Web.Supabase;

public class SupabaseException : Exception
{
    public int StatusCode { get; }

    public SupabaseException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
```

Create `src/HazardRecon.Web/Supabase/SupabaseRestClient.cs`:

```csharp
using System.Net.Http.Headers;

namespace HazardRecon.Web.Supabase;

/// <summary>
/// Shared HTTP surface for PostgREST and Storage. Every call authenticates with
/// the service-role key, which bypasses RLS - so callers are responsible for
/// scoping requests to the authenticated user.
/// </summary>
public class SupabaseRestClient : IDisposable
{
    private readonly SupabaseOptions _options;
    private readonly HttpClient _http;

    public SupabaseRestClient(SupabaseOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
    }

    public async Task<string> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content = null,
        IReadOnlyDictionary<string, string>? headers = null,
        CancellationToken ct = default)
    {
        using HttpRequestMessage request = new(method, _options.BaseUrl + path);
        request.Headers.TryAddWithoutValidation("apikey", _options.ServiceRoleKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServiceRoleKey);

        if (headers != null)
        {
            foreach (KeyValuePair<string, string> h in headers)
            {
                request.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
        }

        request.Content = content;

        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // body only - the request carried the service-role key and must never
            // be echoed into a log or an error surfaced to a caller
            throw new SupabaseException((int)response.StatusCode,
                $"Supabase {(int)response.StatusCode} for {method} {path}: {body}");
        }

        return body;
    }

    public void Dispose() => _http.Dispose();
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/HazardRecon.Tests --filter SupabaseRestClientTests`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Web/Supabase tests/HazardRecon.Tests/Web
git commit -m "feat: add Supabase REST client with typed failures"
```

---

### Task 4: Run store

**Files:**
- Create: `src/HazardRecon.Web/Runs/RunRecord.cs`
- Create: `src/HazardRecon.Web/Runs/IRunStore.cs`
- Create: `src/HazardRecon.Web/Runs/SupabaseRunStore.cs`
- Create: `tests/HazardRecon.Tests/Web/SupabaseRunStoreTests.cs`

**Interfaces:**
- Consumes: `SupabaseRestClient.SendAsync` from Task 3.
- Produces:
  - `public class RunRecord` with `Guid Id`, `Guid UserId`, `string Status`, `string? ModelId`, `List<string> SetLabels`, `string? Error`, `DateTimeOffset CreatedAt`, `DateTimeOffset? StartedAt`, `DateTimeOffset? FinishedAt`.
  - `public interface IRunStore` with:
    - `Task<RunRecord> CreateAsync(Guid userId, IReadOnlyList<string> setLabels, CancellationToken ct = default)`
    - `Task<RunRecord?> GetAsync(Guid runId, Guid userId, CancellationToken ct = default)`
    - `Task<IReadOnlyList<RunRecord>> ListAsync(Guid userId, int limit = 50, CancellationToken ct = default)`
    - `Task UpdateStatusAsync(Guid runId, string status, string? error, CancellationToken ct = default)`
    - `Task<int> CountSinceAsync(Guid userId, DateTimeOffset since, CancellationToken ct = default)`
    - `Task<int> MarkRunningAsInterruptedAsync(CancellationToken ct = default)`
  - `public class SupabaseRunStore : IRunStore` with constructor `(SupabaseRestClient rest)`.

- [ ] **Step 1: Write the failing test**

Create `tests/HazardRecon.Tests/Web/SupabaseRunStoreTests.cs`:

```csharp
using System.Net;
using HazardRecon.Tests.Llm;
using HazardRecon.Web.Runs;
using HazardRecon.Web.Supabase;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseRunStoreTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private const string OneRunJson = """
    [
      {
        "id": "22222222-2222-2222-2222-222222222222",
        "user_id": "11111111-1111-1111-1111-111111111111",
        "status": "ready",
        "model_id": null,
        "set_labels": ["JUN2026 0.5PCT"],
        "error": null,
        "created_at": "2026-07-30T09:00:00+00:00",
        "started_at": null,
        "finished_at": null
      }
    ]
    """;

    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co",
        AnonKey = "anon-key",
        ServiceRoleKey = "service-key"
    };

    private static SupabaseRunStore Store(FakeHttpMessageHandler handler) =>
        new(new SupabaseRestClient(Options(), handler));

    [Fact]
    public async Task TestCreateInsertsAndReturnsTheRow()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.Created, OneRunJson));

        RunRecord run = await Store(handler).CreateAsync(UserId, new[] { "JUN2026 0.5PCT" });

        Assert.Equal(RunId, run.Id);
        Assert.Equal(UserId, run.UserId);
        Assert.Equal("ready", run.Status);
        Assert.Equal(new[] { "JUN2026 0.5PCT" }, run.SetLabels);

        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("https://ref.supabase.co/rest/v1/runs", handler.Requests[0].Url);
        Assert.Contains("JUN2026 0.5PCT", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TestGetFiltersByBothRunAndUser()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, OneRunJson));

        await Store(handler).GetAsync(RunId, UserId);

        string url = handler.Requests[0].Url;
        Assert.Contains($"id=eq.{RunId}", url);
        Assert.Contains($"user_id=eq.{UserId}", url);
    }

    [Fact]
    public async Task TestGetReturnsNullWhenNoRowMatches()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        Assert.Null(await Store(handler).GetAsync(RunId, UserId));
    }

    [Fact]
    public async Task TestListIsScopedToTheUserAndNewestFirst()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, OneRunJson));

        IReadOnlyList<RunRecord> runs = await Store(handler).ListAsync(UserId);

        Assert.Single(runs);
        string url = handler.Requests[0].Url;
        Assert.Contains($"user_id=eq.{UserId}", url);
        Assert.Contains("order=created_at.desc", url);
        Assert.Contains("limit=50", url);
    }

    [Fact]
    public async Task TestUpdateStatusPatchesTheRun()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, OneRunJson));

        await Store(handler).UpdateStatusAsync(RunId, "error", "Boom: it broke");

        Assert.Equal("PATCH", handler.Requests[0].Method);
        Assert.Contains($"id=eq.{RunId}", handler.Requests[0].Url);
        Assert.Contains("\"status\":\"error\"", handler.Requests[0].Body);
        Assert.Contains("Boom: it broke", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TestCountSinceCountsReturnedRows()
    {
        FakeHttpMessageHandler handler = new((_, _) =>
            (HttpStatusCode.OK, """[{"id":"a"},{"id":"b"},{"id":"c"}]"""));

        int count = await Store(handler).CountSinceAsync(
            UserId, new DateTimeOffset(2026, 7, 29, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal(3, count);
        Assert.Contains("created_at=gte.2026-07-29T09%3A00%3A00.0000000%2B00%3A00", handler.Requests[0].Url);
    }

    [Fact]
    public async Task TestMarkRunningAsInterruptedPatchesOnlyRunningRows()
    {
        FakeHttpMessageHandler handler = new((_, _) =>
            (HttpStatusCode.OK, """[{"id":"a"},{"id":"b"}]"""));

        int changed = await Store(handler).MarkRunningAsInterruptedAsync();

        Assert.Equal(2, changed);
        Assert.Equal("PATCH", handler.Requests[0].Method);
        Assert.Contains("status=eq.running", handler.Requests[0].Url);
        Assert.Contains("\"status\":\"interrupted\"", handler.Requests[0].Body);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter SupabaseRunStoreTests`
Expected: FAIL — `HazardRecon.Web.Runs` does not exist.

- [ ] **Step 3: Write the record type**

Create `src/HazardRecon.Web/Runs/RunRecord.cs`:

```csharp
using System.Text.Json.Serialization;

namespace HazardRecon.Web.Runs;

/// <summary>
/// One row of public.runs. Property names map to the snake_case columns
/// PostgREST returns.
/// </summary>
public class RunRecord
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "ready";

    [JsonPropertyName("model_id")]
    public string? ModelId { get; set; }

    [JsonPropertyName("set_labels")]
    public List<string> SetLabels { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("finished_at")]
    public DateTimeOffset? FinishedAt { get; set; }
}
```

- [ ] **Step 4: Write the interface**

Create `src/HazardRecon.Web/Runs/IRunStore.cs`:

```csharp
namespace HazardRecon.Web.Runs;

/// <summary>
/// Persistence for runs. Every read is scoped by user id: callers pass the sub
/// claim of a verified token, never a value from the request body.
/// </summary>
public interface IRunStore
{
    Task<RunRecord> CreateAsync(Guid userId, IReadOnlyList<string> setLabels, CancellationToken ct = default);

    Task<RunRecord?> GetAsync(Guid runId, Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<RunRecord>> ListAsync(Guid userId, int limit = 50, CancellationToken ct = default);

    Task UpdateStatusAsync(Guid runId, string status, string? error, CancellationToken ct = default);

    Task<int> CountSinceAsync(Guid userId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>
    /// Marks every row still flagged running as interrupted. Called once at
    /// startup: a restart killed those runs and nothing will ever finish them.
    /// </summary>
    Task<int> MarkRunningAsInterruptedAsync(CancellationToken ct = default);
}
```

- [ ] **Step 5: Write the implementation**

Create `src/HazardRecon.Web/Runs/SupabaseRunStore.cs`:

```csharp
using System.Text;
using System.Text.Json;
using HazardRecon.Web.Supabase;

namespace HazardRecon.Web.Runs;

public class SupabaseRunStore : IRunStore
{
    private const string Table = "/rest/v1/runs";

    private static readonly Dictionary<string, string> ReturnRow =
        new() { ["Prefer"] = "return=representation" };

    private readonly SupabaseRestClient _rest;

    public SupabaseRunStore(SupabaseRestClient rest) => _rest = rest;

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static List<RunRecord> Parse(string body) =>
        JsonSerializer.Deserialize<List<RunRecord>>(body) ?? new List<RunRecord>();

    public async Task<RunRecord> CreateAsync(Guid userId, IReadOnlyList<string> setLabels, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Post, Table,
            Json(new { user_id = userId, status = "ready", set_labels = setLabels }),
            ReturnRow, ct);

        List<RunRecord> rows = Parse(body);
        if (rows.Count == 0)
        {
            throw new SupabaseException(500, "Insert into runs returned no row.");
        }

        return rows[0];
    }

    public async Task<RunRecord?> GetAsync(Guid runId, Guid userId, CancellationToken ct = default)
    {
        // filtered by user as well as id: an unknown owner must look identical to
        // a missing run, so the endpoint can answer 404 rather than 403
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{Table}?id=eq.{runId}&user_id=eq.{userId}&select=*", null, null, ct);

        return Parse(body).FirstOrDefault();
    }

    public async Task<IReadOnlyList<RunRecord>> ListAsync(Guid userId, int limit = 50, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{Table}?user_id=eq.{userId}&select=*&order=created_at.desc&limit={limit}", null, null, ct);

        return Parse(body);
    }

    public async Task UpdateStatusAsync(Guid runId, string status, string? error, CancellationToken ct = default)
    {
        Dictionary<string, object?> patch = new()
        {
            ["status"] = status,
            ["error"] = error
        };

        if (status == "running") patch["started_at"] = DateTimeOffset.UtcNow;
        if (status is "done" or "error" or "interrupted") patch["finished_at"] = DateTimeOffset.UtcNow;

        await _rest.SendAsync(HttpMethod.Patch, $"{Table}?id=eq.{runId}",
            Json(patch), ReturnRow, ct);
    }

    public async Task<int> CountSinceAsync(Guid userId, DateTimeOffset since, CancellationToken ct = default)
    {
        string encoded = Uri.EscapeDataString(since.ToString("O"));
        string body = await _rest.SendAsync(HttpMethod.Get,
            $"{Table}?user_id=eq.{userId}&created_at=gte.{encoded}&select=id", null, null, ct);

        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetArrayLength();
    }

    public async Task<int> MarkRunningAsInterruptedAsync(CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Patch,
            $"{Table}?status=eq.running",
            Json(new { status = "interrupted", finished_at = DateTimeOffset.UtcNow }),
            ReturnRow, ct);

        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetArrayLength();
    }
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/HazardRecon.Tests --filter SupabaseRunStoreTests`
Expected: PASS, 7 tests.

If `TestCountSinceCountsReturnedRows` fails on the encoded timestamp, print `handler.Requests[0].Url` and align the expected string with what `Uri.EscapeDataString(since.ToString("O"))` actually produces — the assertion documents the real encoding, so fix the test to match the implementation here, not the other way round.

- [ ] **Step 7: Commit**

```bash
git add src/HazardRecon.Web/Runs tests/HazardRecon.Tests/Web
git commit -m "feat: add Supabase-backed run store"
```

---

### Task 5: File store

**Files:**
- Create: `src/HazardRecon.Web/Files/IFileStore.cs`
- Create: `src/HazardRecon.Web/Files/SupabaseFileStore.cs`
- Create: `tests/HazardRecon.Tests/Web/SupabaseFileStoreTests.cs`

**Interfaces:**
- Consumes: `SupabaseRestClient.SendAsync` from Task 3.
- Produces: `public interface IFileStore` with:
  - `Task UploadAsync(string storagePath, Stream content, string contentType, CancellationToken ct = default)`
  - `Task<string> CreateSignedUrlAsync(string storagePath, int expiresInSeconds, CancellationToken ct = default)` — returns an absolute URL
  - `Task DeletePrefixAsync(string prefix, CancellationToken ct = default)`
  and `public class SupabaseFileStore : IFileStore` with constructor `(SupabaseRestClient rest, SupabaseOptions options, string bucket = "runs")`.

- [ ] **Step 1: Write the failing test**

Create `tests/HazardRecon.Tests/Web/SupabaseFileStoreTests.cs`:

```csharp
using System.Net;
using System.Text;
using HazardRecon.Tests.Llm;
using HazardRecon.Web.Files;
using HazardRecon.Web.Supabase;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseFileStoreTests
{
    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co",
        AnonKey = "anon-key",
        ServiceRoleKey = "service-key"
    };

    private static SupabaseFileStore Store(FakeHttpMessageHandler handler) =>
        new(new SupabaseRestClient(Options(), handler), Options());

    [Fact]
    public async Task TestUploadPostsToTheObjectPath()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "{}"));
        using MemoryStream content = new(Encoding.UTF8.GetBytes("col_a,col_b\n1,2\n"));

        await Store(handler).UploadAsync("user/run/output/report.csv", content, "text/csv");

        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("https://ref.supabase.co/storage/v1/object/runs/user/run/output/report.csv",
            handler.Requests[0].Url);
        Assert.Contains("col_a,col_b", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TestSignedUrlIsReturnedAbsolute()
    {
        FakeHttpMessageHandler handler = new((_, _) =>
            (HttpStatusCode.OK, """{"signedURL":"/object/sign/runs/user/run/output/report.csv?token=abc"}"""));

        string url = await Store(handler).CreateSignedUrlAsync("user/run/output/report.csv", 60);

        Assert.Equal(
            "https://ref.supabase.co/storage/v1/object/sign/runs/user/run/output/report.csv?token=abc",
            url);
    }

    [Fact]
    public async Task TestSignedUrlRequestCarriesTheExpiry()
    {
        FakeHttpMessageHandler handler = new((_, _) =>
            (HttpStatusCode.OK, """{"signedURL":"/object/sign/runs/x?token=t"}"""));

        await Store(handler).CreateSignedUrlAsync("x", 60);

        Assert.Equal("https://ref.supabase.co/storage/v1/object/sign/runs/x", handler.Requests[0].Url);
        Assert.Contains("\"expiresIn\":60", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TestDeletePrefixListsThenDeletesEveryObjectFound()
    {
        FakeHttpMessageHandler handler = new((req, i) =>
            i == 0
                ? (HttpStatusCode.OK, """[{"name":"a.csv"},{"name":"b.csv"}]""")
                : (HttpStatusCode.OK, "[]"));

        await Store(handler).DeletePrefixAsync("user/run/input");

        Assert.Equal("https://ref.supabase.co/storage/v1/object/list/runs", handler.Requests[0].Url);
        Assert.Contains("user/run/input", handler.Requests[0].Body);

        Assert.Equal("DELETE", handler.Requests[1].Method);
        Assert.Contains("user/run/input/a.csv", handler.Requests[1].Body);
        Assert.Contains("user/run/input/b.csv", handler.Requests[1].Body);
    }

    [Fact]
    public async Task TestDeletePrefixSkipsTheDeleteWhenNothingMatches()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        await Store(handler).DeletePrefixAsync("user/run/input");

        Assert.Single(handler.Requests);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter SupabaseFileStoreTests`
Expected: FAIL — `HazardRecon.Web.Files` does not exist.

- [ ] **Step 3: Write the interface**

Create `src/HazardRecon.Web/Files/IFileStore.cs`:

```csharp
namespace HazardRecon.Web.Files;

/// <summary>
/// Object storage for run inputs and outputs. The bucket is private: nothing is
/// ever served from it directly, only through short-lived signed URLs.
/// </summary>
public interface IFileStore
{
    Task UploadAsync(string storagePath, Stream content, string contentType, CancellationToken ct = default);

    Task<string> CreateSignedUrlAsync(string storagePath, int expiresInSeconds, CancellationToken ct = default);

    Task DeletePrefixAsync(string prefix, CancellationToken ct = default);
}
```

- [ ] **Step 4: Write the implementation**

Create `src/HazardRecon.Web/Files/SupabaseFileStore.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HazardRecon.Web.Supabase;

namespace HazardRecon.Web.Files;

public class SupabaseFileStore : IFileStore
{
    private readonly SupabaseRestClient _rest;
    private readonly SupabaseOptions _options;
    private readonly string _bucket;

    public SupabaseFileStore(SupabaseRestClient rest, SupabaseOptions options, string bucket = "runs")
    {
        _rest = rest;
        _options = options;
        _bucket = bucket;
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    public async Task UploadAsync(string storagePath, Stream content, string contentType, CancellationToken ct = default)
    {
        StreamContent body = new(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        await _rest.SendAsync(HttpMethod.Post,
            $"/storage/v1/object/{_bucket}/{storagePath}",
            body,
            new Dictionary<string, string> { ["x-upsert"] = "true" },
            ct);
    }

    public async Task<string> CreateSignedUrlAsync(string storagePath, int expiresInSeconds, CancellationToken ct = default)
    {
        string body = await _rest.SendAsync(HttpMethod.Post,
            $"/storage/v1/object/sign/{_bucket}/{storagePath}",
            Json(new { expiresIn = expiresInSeconds }), null, ct);

        using JsonDocument doc = JsonDocument.Parse(body);
        string relative = doc.RootElement.GetProperty("signedURL").GetString()
            ?? throw new SupabaseException(500, "Sign response carried no signedURL.");

        // Supabase returns a path relative to /storage/v1
        return $"{_options.BaseUrl}/storage/v1{relative}";
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct = default)
    {
        string listBody = await _rest.SendAsync(HttpMethod.Post,
            $"/storage/v1/object/list/{_bucket}",
            Json(new { prefix, limit = 1000 }), null, ct);

        List<string> paths = new();
        using (JsonDocument doc = JsonDocument.Parse(listBody))
        {
            foreach (JsonElement item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("name", out JsonElement name) && name.GetString() is string n)
                {
                    paths.Add($"{prefix}/{n}");
                }
            }
        }

        if (paths.Count == 0) return;

        await _rest.SendAsync(HttpMethod.Delete,
            $"/storage/v1/object/{_bucket}",
            Json(new { prefixes = paths }), null, ct);
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/HazardRecon.Tests --filter SupabaseFileStoreTests`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```bash
git add src/HazardRecon.Web/Files tests/HazardRecon.Tests/Web
git commit -m "feat: add Supabase-backed file store"
```

---

### Task 6: JWT validation and authenticated endpoints

**Files:**
- Create: `src/HazardRecon.Web/Supabase/SupabaseJwt.cs`
- Create: `tests/HazardRecon.Tests/Web/SupabaseJwtTests.cs`
- Create: `tests/HazardRecon.Tests/Web/AuthEndpointTests.cs`
- Modify: `src/HazardRecon.Web/HazardRecon.Web.csproj` (add JwtBearer package)
- Modify: `tests/HazardRecon.Tests/HazardRecon.Tests.csproj` (add Mvc.Testing + JWT packages)
- Modify: `src/HazardRecon.Web/Program.cs` (auth wiring, `/api/config`, `.RequireAuthorization()`, `public partial class Program`)

**Interfaces:**
- Consumes: `SupabaseOptions` from Task 2.
- Produces: `public static class SupabaseJwt` with
  `static TokenValidationParameters BuildValidationParameters(SupabaseOptions options)` and
  `static Guid? UserId(ClaimsPrincipal principal)` which reads the `sub` claim.

- [ ] **Step 1: Add the packages**

To `src/HazardRecon.Web/HazardRecon.Web.csproj`, inside the existing `ItemGroup` with the project reference, add:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.0" />
```

To `tests/HazardRecon.Tests/HazardRecon.Tests.csproj`, in the package `ItemGroup`, add:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="8.14.0" />
```

Run `dotnet restore`. If version 10.0.0 does not resolve, run `dotnet list package --outdated` and use the current `10.*` release — record the version you used in the commit message.

- [ ] **Step 2: Write the failing test for token validation**

Create `tests/HazardRecon.Tests/Web/SupabaseJwtTests.cs`. These tests sign tokens with a locally generated RSA key and validate them against the real parameters, so they exercise signature, issuer, audience, and lifetime with no network:

```csharp
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using HazardRecon.Web.Supabase;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseJwtTests
{
    private static readonly RSA Key = RSA.Create(2048);
    private static readonly RSA OtherKey = RSA.Create(2048);

    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co",
        AnonKey = "anon-key",
        ServiceRoleKey = "service-key"
    };

    private static string Token(
        RSA key,
        string issuer = "https://ref.supabase.co/auth/v1",
        string audience = "authenticated",
        int minutesValid = 60,
        string subject = "11111111-1111-1111-1111-111111111111")
    {
        SigningCredentials creds = new(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256);
        JwtSecurityToken token = new(
            issuer: issuer,
            audience: audience,
            claims: new[] { new Claim("sub", subject) },
            notBefore: DateTime.UtcNow.AddMinutes(-5),
            expires: DateTime.UtcNow.AddMinutes(minutesValid),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// The real parameters, with the signing key pinned locally instead of
    /// fetched from JWKS - everything else under test is the production config.
    /// </summary>
    private static TokenValidationParameters Parameters(RSA key)
    {
        TokenValidationParameters p = SupabaseJwt.BuildValidationParameters(Options());
        p.IssuerSigningKey = new RsaSecurityKey(key);
        p.IssuerSigningKeyResolver = null;
        return p;
    }

    private static ClaimsPrincipal Validate(string token, RSA key) =>
        new JwtSecurityTokenHandler().ValidateToken(token, Parameters(key), out _);

    [Fact]
    public void TestAValidTokenIsAccepted()
    {
        ClaimsPrincipal principal = Validate(Token(Key), Key);

        Assert.Equal(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SupabaseJwt.UserId(principal));
    }

    [Fact]
    public void TestATokenSignedByAnotherKeyIsRejected()
    {
        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(
            () => Validate(Token(OtherKey), Key));
    }

    [Fact]
    public void TestAnExpiredTokenIsRejected()
    {
        Assert.Throws<SecurityTokenExpiredException>(
            () => Validate(Token(Key, minutesValid: -10), Key));
    }

    [Fact]
    public void TestATokenFromAnotherIssuerIsRejected()
    {
        Assert.Throws<SecurityTokenInvalidIssuerException>(
            () => Validate(Token(Key, issuer: "https://evil.example/auth/v1"), Key));
    }

    [Fact]
    public void TestATokenForAnotherAudienceIsRejected()
    {
        Assert.Throws<SecurityTokenInvalidAudienceException>(
            () => Validate(Token(Key, audience: "anon"), Key));
    }

    [Fact]
    public void TestTheIssuerIsDerivedFromTheProjectUrl()
    {
        TokenValidationParameters p = SupabaseJwt.BuildValidationParameters(Options());

        Assert.Contains("https://ref.supabase.co/auth/v1", p.ValidIssuers!);
        Assert.True(p.ValidateLifetime);
        Assert.True(p.ValidateIssuerSigningKey);
    }

    [Fact]
    public void TestUserIdIsNullWhenTheSubClaimIsAbsent()
    {
        Assert.Null(SupabaseJwt.UserId(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/HazardRecon.Tests --filter SupabaseJwtTests`
Expected: FAIL — `SupabaseJwt` does not exist.

- [ ] **Step 4: Write the implementation**

Create `src/HazardRecon.Web/Supabase/SupabaseJwt.cs`:

```csharp
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace HazardRecon.Web.Supabase;

public static class SupabaseJwt
{
    /// <summary>The GoTrue issuer and audience for a Supabase project.</summary>
    public static string Issuer(SupabaseOptions options) => $"{options.BaseUrl}/auth/v1";

    public static TokenValidationParameters BuildValidationParameters(SupabaseOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuers = new[] { Issuer(options) },
        ValidateAudience = true,
        ValidAudiences = new[] { "authenticated" },
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub"
    };

    /// <summary>
    /// The authenticated user's id. Every data access scopes on this value and
    /// never on anything from a request body.
    /// </summary>
    public static Guid? UserId(ClaimsPrincipal principal)
    {
        string? sub = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return Guid.TryParse(sub, out Guid id) ? id : null;
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test tests/HazardRecon.Tests --filter SupabaseJwtTests`
Expected: PASS, 7 tests.

- [ ] **Step 6: Wire authentication into Program.cs**

In `src/HazardRecon.Web/Program.cs`, add these usings:

```csharp
using Microsoft.AspNetCore.Authentication.JwtBearer;
using HazardRecon.Web.Supabase;
```

After the fail-fast block from Task 2 and **before** `var app = builder.Build();`, add:

```csharp
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Supabase signs with rotating asymmetric keys published as a JWKS.
        options.Authority = SupabaseJwt.Issuer(supabaseOptions);
        options.TokenValidationParameters = SupabaseJwt.BuildValidationParameters(supabaseOptions);
    });

builder.Services.AddAuthorization();
```

**If Task 1 step 3 recorded that the OpenID configuration document does NOT exist**, delete the `options.Authority` line and instead point a configuration manager straight at the JWKS. First create `src/HazardRecon.Web/Supabase/JwksRetriever.cs`:

```csharp
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace HazardRecon.Web.Supabase;

/// <summary>
/// Reads a bare JWKS document into an OpenIdConnectConfiguration. Needed because
/// the project publishes a key set but no OpenID discovery document, so the
/// stock retriever has nothing to read.
/// </summary>
public class JwksRetriever : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        string json = await retriever.GetDocumentAsync(address, cancel);
        OpenIdConnectConfiguration config = new();

        foreach (JsonWebKey key in new JsonWebKeySet(json).GetSigningKeys().Cast<JsonWebKey>())
        {
            config.JsonWebKeySet ??= new JsonWebKeySet();
            config.SigningKeys.Add(key);
        }

        return config;
    }
}
```

Then use it in the JwtBearer options:

```csharp
        options.ConfigurationManager =
            new ConfigurationManager<OpenIdConnectConfiguration>(
                $"{SupabaseJwt.Issuer(supabaseOptions)}/.well-known/jwks.json",
                new JwksRetriever(),
                new HttpDocumentRetriever());
```

with `using Microsoft.IdentityModel.Protocols;` and `using Microsoft.IdentityModel.Protocols.OpenIdConnect;` added at the top of `Program.cs`. Write this file only if step 3 showed you need it; if the discovery document exists, the `Authority` line above is sufficient and this class should not exist.

Then, immediately after `var app = builder.Build();` and before `app.UseDefaultFiles();`, add:

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

- [ ] **Step 7: Add the config endpoint and require auth on the rest**

In `src/HazardRecon.Web/Program.cs`, add near the `/health` endpoint:

```csharp
// GET /api/config - the browser needs the project URL and the public anon key to
// start a session. The service-role key is never exposed here.
app.MapGet("/api/config", () => Results.Ok(new
{
    supabaseUrl = supabaseOptions.BaseUrl,
    supabaseAnonKey = supabaseOptions.AnonKey
}));
```

Append `.RequireAuthorization()` to every existing `app.Map*` call **except** `/health` and `/api/config`: that is `/api/discover`, `/api/run`, `/api/job/{rid}`, `/api/chat`, `/runs/{rid}/output/{filename}`, and `/api/models`.

For example, line 307's health endpoint stays as-is, while the models endpoint becomes:

```csharp
app.MapGet("/api/models", async () =>
{
    // ... unchanged body ...
}).RequireAuthorization();
```

- [ ] **Step 8: Expose Program for integration tests**

At the very end of `src/HazardRecon.Web/Program.cs`, after `return 0;`, add:

```csharp
// exposed so WebApplicationFactory can boot the real app in tests
public partial class Program { }
```

- [ ] **Step 9: Write the endpoint authorization tests**

Create `tests/HazardRecon.Tests/Web/AuthEndpointTests.cs`. These boot the real app and assert the authorization boundary. They deliberately send **no** token, so no JWKS fetch is ever attempted and the tests stay offline:

```csharp
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace HazardRecon.Tests.Web;

public class AuthEndpointTests : IClassFixture<AuthEndpointTests.Factory>
{
    public class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Supabase:Url"] = "https://ref.supabase.co",
                    ["Supabase:AnonKey"] = "anon-key-for-tests",
                    ["Supabase:ServiceRoleKey"] = "service-key-for-tests"
                }));

            return base.CreateHost(builder);
        }
    }

    private readonly Factory _factory;

    public AuthEndpointTests(Factory factory) => _factory = factory;

    [Theory]
    [InlineData("/api/models")]
    [InlineData("/api/job/anything")]
    public async Task TestProtectedEndpointsRejectAnAnonymousCaller(string path)
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TestHealthStaysOpen()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TestConfigServesTheAnonKeyButNeverTheServiceRoleKey()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/config");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("anon-key-for-tests", body);
        Assert.DoesNotContain("service-key-for-tests", body);
    }
}
```

Add `using Microsoft.Extensions.Configuration;` and `using Microsoft.Extensions.Hosting;` if the compiler asks for them.

- [ ] **Step 10: Run the tests**

Run: `dotnet test tests/HazardRecon.Tests --filter AuthEndpointTests`
Expected: PASS, 4 tests.

- [ ] **Step 11: Run the whole suite**

Run: `dotnet test`
Expected: PASS — all pre-existing tests still green.

- [ ] **Step 12: Commit**

```bash
git add src/HazardRecon.Web tests/HazardRecon.Tests
git commit -m "feat: validate Supabase tokens and require auth on the API"
```

---

### Task 7: Browser login gate

**Files:**
- Modify: `src/HazardRecon.Web/wwwroot/index.html` (supabase-js script, auth gate markup)
- Modify: `src/HazardRecon.Web/wwwroot/app.js` (session bootstrap, `api()` helper, sign-out)
- Modify: `src/HazardRecon.Web/wwwroot/app.css` (auth gate styles)
- Modify: `tests/client/app.harness.mjs` (stub `supabase`, add auth scenarios)

**Interfaces:**
- Consumes: `GET /api/config` from Task 6; the `Authorization: Bearer` requirement on every other endpoint.
- Produces: a global `api(path, options)` in `app.js` that injects the bearer token, used by every later fetch.

- [ ] **Step 1: Add supabase-js and the auth gate markup**

In `src/HazardRecon.Web/wwwroot/index.html`, before `<script src="app.js"></script>`, add:

```html
<script src="https://cdn.jsdelivr.net/npm/@supabase/supabase-js@2/dist/umd/supabase.js"></script>
```

Immediately after `<body>`, add the gate — it covers the page until a session exists:

```html
<div id="auth-gate" class="authgate">
  <div class="authcard">
    <h1>Hazard-Rate Reconciliation</h1>
    <p class="hint">Sign in to run a reconciliation and see your history.</p>
    <label for="auth-email">Email</label>
    <input type="email" id="auth-email" autocomplete="email">
    <label for="auth-password">Password</label>
    <input type="password" id="auth-password" autocomplete="current-password">
    <div class="actions">
      <button class="btn primary" id="btn-signin">Sign in</button>
      <button class="btn" id="btn-signup">Create account</button>
    </div>
    <p class="authmsg" id="auth-msg"></p>
  </div>
</div>
```

Replace the contents of the existing `<header>`'s `.wrap` (`index.html:12-15`) so the title and tagline sit in one element and the sign-out button becomes its sibling — the CSS in step 6 relies on this shape:

```html
  <div class="wrap">
    <div>
      <h1>Hazard-Rate Reconciliation</h1>
      <p>Anchor Point Risk &middot; reconcile engine defaults against write-offs and IFRS9, and review the model output</p>
    </div>
    <button class="btn" id="btn-signout">Sign out</button>
  </div>
```

- [ ] **Step 2: Write the failing harness scenarios**

In `tests/client/app.harness.mjs`, add a `supabase` stub to the `ctx` object inside `newCtx()`, next to the existing `fetch: null` line:

```js
    supabase: {
      _session: null,
      _signInError: null,
      createClient() {
        const self = this;
        return {
          auth: {
            getSession: () => Promise.resolve({ data: { session: self._session } }),
            signInWithPassword: ({ email }) =>
              Promise.resolve(self._signInError
                ? { data: { session: null }, error: { message: self._signInError } }
                : { data: { session: { access_token: "tok-" + email } }, error: null }),
            signUp: () => Promise.resolve({ data: { session: null }, error: null }),
            signOut: () => { self._session = null; return Promise.resolve({ error: null }); },
            onAuthStateChange: () => ({ data: { subscription: { unsubscribe() {} } } }),
          },
        };
      },
    },
```

Then add these scenarios alongside the existing ones, following their established shape:

```js
// scenarioAuth1: with no session the gate is visible and the app is hidden
async function scenarioNoSessionShowsGate() {
  const { ctx, $get } = newCtx();
  ctx.supabase._session = null;
  ctx.fetch = (url) =>
    Promise.resolve(mkRes(200, JSON.stringify({ supabaseUrl: "https://x.supabase.co", supabaseAnonKey: "k" })));

  vm.runInContext(SRC, ctx);
  await flush();

  assert(!$get("#auth-gate").classList.contains("hide"), "gate should be visible with no session");
}

// scenarioAuth2: an existing session hides the gate
async function scenarioSessionHidesGate() {
  const { ctx, $get } = newCtx();
  ctx.supabase._session = { access_token: "tok-abc" };
  ctx.fetch = () =>
    Promise.resolve(mkRes(200, JSON.stringify({ supabaseUrl: "https://x.supabase.co", supabaseAnonKey: "k" })));

  vm.runInContext(SRC, ctx);
  await flush();

  assert($get("#auth-gate").classList.contains("hide"), "gate should be hidden with a session");
}

// scenarioAuth3: every API call carries the bearer token
async function scenarioApiCallsCarryTheToken() {
  const { ctx } = newCtx();
  ctx.supabase._session = { access_token: "tok-abc" };
  const seen = [];
  ctx.fetch = (url, opts) => {
    seen.push({ url, auth: opts && opts.headers && opts.headers.Authorization });
    return Promise.resolve(mkRes(200, JSON.stringify({ supabaseUrl: "u", supabaseAnonKey: "k" })));
  };

  vm.runInContext(SRC, ctx);
  await flush();

  const models = seen.find(s => String(s.url).includes("/api/models"));
  assert(models && models.auth === "Bearer tok-abc",
    "the models call should carry the bearer token, saw: " + JSON.stringify(models));
}
```

Register the three scenarios in the harness's runner list, matching how the existing scenarios are registered. If the harness has no `flush()` helper, add `const flush = () => new Promise(r => setImmediate(r));` near the top.

- [ ] **Step 3: Run the harness to verify it fails**

Run: `node tests/client/app.harness.mjs`
Expected: FAIL on the three new scenarios — `app.js` has no auth gate yet. The pre-existing scenarios must still pass.

- [ ] **Step 4: Implement the session bootstrap in app.js**

At the top of `src/HazardRecon.Web/wwwroot/app.js`, after the existing `$`/`el`/`fmt` helpers, add:

```js
/* ---------- session ---------- */
let SB = null;          // supabase client
let TOKEN = null;       // current access token

/* Every API call goes through here: the token is injected, and a 401 means the
   session died underneath us, so we drop back to the gate rather than leaving
   the UI wedged. */
function api(path, options) {
  const opts = options || {};
  const headers = Object.assign({}, opts.headers || {});
  if (TOKEN) headers.Authorization = "Bearer " + TOKEN;
  return fetch(path, Object.assign({}, opts, { headers })).then((r) => {
    if (r.status === 401) { showGate("Your session expired - please sign in again."); }
    return r;
  });
}

function showGate(message) {
  TOKEN = null;
  $("#auth-gate").classList.remove("hide");
  if (message) $("#auth-msg").textContent = message;
}

function hideGate() {
  $("#auth-gate").classList.add("hide");
  $("#auth-msg").textContent = "";
}

async function startSession() {
  const res = await fetch("/api/config");
  const cfg = await res.json();
  SB = supabase.createClient(cfg.supabaseUrl, cfg.supabaseAnonKey);

  const { data } = await SB.auth.getSession();
  if (data && data.session) {
    TOKEN = data.session.access_token;
    hideGate();
    loadModels();
  } else {
    showGate("");
  }
}

$("#btn-signin").addEventListener("click", async () => {
  const { data, error } = await SB.auth.signInWithPassword({
    email: $("#auth-email").value.trim(),
    password: $("#auth-password").value,
  });
  if (error) { $("#auth-msg").textContent = error.message; return; }
  TOKEN = data.session.access_token;
  hideGate();
  loadModels();
});

$("#btn-signup").addEventListener("click", async () => {
  const { error } = await SB.auth.signUp({
    email: $("#auth-email").value.trim(),
    password: $("#auth-password").value,
  });
  $("#auth-msg").textContent = error
    ? error.message
    : "Check your email for a confirmation link, then sign in.";
});

$("#btn-signout").addEventListener("click", async () => {
  await SB.auth.signOut();
  showGate("Signed out.");
});

startSession();
```

- [ ] **Step 5: Route the existing fetches through api()**

In `src/HazardRecon.Web/wwwroot/app.js`, replace every `fetch(` call that targets an `/api/` or `/runs/` path with `api(`. Leave the `fetch("/api/config")` call inside `startSession` alone — it is deliberately unauthenticated.

Remove the bare `loadModels()` invocation that currently runs at load time: models now load only after a session exists, from `startSession` and the sign-in handler.

- [ ] **Step 6: Style the gate**

In `src/HazardRecon.Web/wwwroot/app.css`, append:

```css
/* auth gate - covers the page until a session exists */
.authgate{position:fixed;inset:0;z-index:50;background:var(--bg);
     display:grid;place-items:center;padding:22px}
.authcard{background:var(--card);border:1px solid var(--line);border-radius:12px;
     padding:26px 28px;width:100%;max-width:380px;
     box-shadow:0 1px 3px rgba(20,40,70,.05)}
.authcard h1{margin:0 0 4px;font-size:20px;font-weight:650}
.authcard label{display:block;font-size:12.5px;color:var(--muted);
     margin:14px 0 6px}
.authcard input[type=email],.authcard input[type=password]{width:100%;
     padding:9px 11px;border:1px solid #cfd8e3;border-radius:7px;
     font-size:13px;font-family:inherit}
.authcard input[type=email]:focus,.authcard input[type=password]:focus{
     outline:2px solid #bcd4ec;border-color:var(--blue)}
.authmsg{color:var(--muted);font-size:12.5px;margin:14px 0 0;min-height:18px}

/* sign-out sits opposite the title in the dark header */
header .wrap{display:flex;align-items:center;gap:16px;flex-wrap:wrap}
header .wrap > div{flex:1}
#btn-signout{flex:none}
```

Note `.hide` already exists at `app.css:24` and the existing custom properties are defined at `app.css:1-3` — reuse both rather than introducing new colours.

The header rule assumes the title and tagline are wrapped in a single element inside `header .wrap`. Check `index.html:12-15`: they are direct children of `.wrap`, so wrap the existing `<h1>` and `<p>` in a `<div>` when you add the sign-out button in step 1.

- [ ] **Step 7: Run the harness**

Run: `node tests/client/app.harness.mjs`
Expected: PASS — all scenarios, old and new.

- [ ] **Step 8: Verify by hand in a browser**

Start the app with real Supabase values:

```bash
Supabase__Url=https://<ref>.supabase.co \
Supabase__AnonKey=<anon> \
Supabase__ServiceRoleKey=<service> \
  dotnet run --project src/HazardRecon.Web
```

Confirm, in order: the gate appears on load; "Create account" reports the confirmation-email message; after confirming and signing in the gate disappears and the model dropdown populates; a full reconciliation on a local folder still runs end to end; sign-out returns the gate; and reloading while signed in skips the gate.

The pre-existing follow-up about a real click reaching `localStorage` (`docs/superpowers/2026-07-29-cyte-llm-follow-ups.md`) still needs a human here — this is that moment.

- [ ] **Step 9: Run everything**

Run: `dotnet test && node tests/client/app.harness.mjs`
Expected: PASS on both.

- [ ] **Step 10: Commit**

```bash
git add src/HazardRecon.Web/wwwroot tests/client
git commit -m "feat: gate the browser app behind a Supabase login"
```

---

### Task 8: Render deployment and documentation

**Files:**
- Create: `render.yaml`
- Create: `docs/deployment.md`
- Modify: `src/HazardRecon.Web/Program.cs` (startup banner states the single-instance assumption)

**Interfaces:**
- Consumes: everything above.
- Produces: a deployable service definition.

- [ ] **Step 1: State the single-instance assumption at startup**

The spec records this as an open risk: scaling past one instance silently breaks progress logs and spuriously marks live runs interrupted, with no error pointing at it. Make it visible. In `src/HazardRecon.Web/Program.cs`, in the banner block near line 309, add a line:

```csharp
Console.WriteLine(" Single instance only - run history assumes one process owns the live job cache");
```

- [ ] **Step 2: Write the Render service definition**

Create `render.yaml`:

```yaml
services:
  - type: web
    name: hazard-recon
    runtime: docker
    plan: starter
    # The in-process job cache assumes exactly one instance. See
    # docs/superpowers/specs/2026-07-30-supabase-auth-and-run-persistence-design.md
    numInstances: 1
    envVars:
      - key: PORT
        value: 10000
      - key: HOST
        value: 0.0.0.0
      - key: ASPNETCORE_ENVIRONMENT
        value: Production
      - key: Supabase__Url
        sync: false
      - key: Supabase__AnonKey
        sync: false
      - key: Supabase__ServiceRoleKey
        sync: false
      - key: CyteLlm__ClientId
        sync: false
      - key: CyteLlm__ClientSecret
        sync: false
```

`sync: false` means Render prompts for the value rather than storing it in the repo. Note that `Program.cs:11-12` already reads `HOST` and `PORT`, so no code change is needed for binding.

- [ ] **Step 3: Write the deployment doc**

Create `docs/deployment.md` covering: the five environment variables and where each comes from in the Supabase dashboard; that `Supabase__ServiceRoleKey` is a secret that must never reach the browser or a log; the single-instance constraint and why; and the note from `docs/superpowers/2026-07-29-cyte-llm-follow-ups.md` that the web host only registers user secrets in Development, so production configuration must come from environment variables — which is exactly what Render supplies.

- [ ] **Step 4: Verify the app boots the way Render will run it**

```bash
ASPNETCORE_ENVIRONMENT=Production HOST=0.0.0.0 PORT=10000 \
Supabase__Url=https://<ref>.supabase.co \
Supabase__AnonKey=<anon> \
Supabase__ServiceRoleKey=<service> \
CyteLlm__ClientId=<id> CyteLlm__ClientSecret=<secret> \
  dotnet run --project src/HazardRecon.Web
```

Expected: the banner including the single-instance line, no "Supabase is not configured" message, and no "CyteLlm not set" warning. Confirm `http://localhost:10000/health` returns `{"ok":true,...}`.

This also proves the user-secrets gap is closed in Production, which the previous branch left open.

- [ ] **Step 5: Commit**

```bash
git add render.yaml docs/deployment.md src/HazardRecon.Web/Program.cs
git commit -m "chore: add Render service definition and deployment notes"
```

---

## What this plan deliberately leaves undone

These are spec requirements that belong to the second plan (build-order steps 4–7). Listed so a reviewer can confirm the omission is deliberate rather than an oversight:

- Folder upload replacing pasted paths (`POST /api/runs`, path sanitisation, size and file-count caps).
- Persisting run metadata, logs, results, files, and chat to the stores built here.
- `run_files` and `chat_messages` writes — the tables and RLS exist, nothing writes to them yet.
- Signed-URL downloads replacing local file serving.
- Startup reconciliation actually calling `MarkRunningAsInterruptedAsync` — the method exists and is tested, but nothing calls it until runs are persisted.
- The run-history UI.
- The 30-day input purge.
- The per-user run quota — `CountSinceAsync` exists and is tested, but no endpoint enforces it yet.
- `FakeRunStore` and `FakeFileStore`. The spec lists them under step 2, but nothing in this plan consumes `IRunStore` or `IFileStore` from an endpoint, so a fake would be untested code with no caller. The real implementations are covered here through `FakeHttpMessageHandler`; the fakes arrive in the second plan alongside the endpoint tests that need them.

At the end of this plan the app requires a login and otherwise behaves exactly as it does today.
