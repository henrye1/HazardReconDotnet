# Cyte LLM Gateway + User-Selected Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the hard-coded Anthropic API call with the Cyte LLM gateway and let the user pick which model runs the AI analysis and the "Ask about this run" chat.

**Architecture:** A new `ILlmClient` abstraction in `HazardRecon.Core` with one implementation, `CyteLlmClient`, that owns client-credentials token caching, model listing, and chat. `AiAnalysisService` and `ChatService` stop reading environment variables and instead take an `ILlmClient` plus a model id, which makes both testable against a fake HTTP handler. The web UI gains a model dropdown whose selection is stored on the job so the chat reuses the same model.

**Tech Stack:** .NET 10, xUnit, `System.Text.Json`, `HttpClient` with a custom `HttpMessageHandler` for tests, `Microsoft.Extensions.Configuration.UserSecrets`.

## Global Constraints

- Target framework is `net10.0` for every project. Do not change it.
- `HazardRecon.Core` must NOT take a dependency on `Microsoft.Extensions.*`. Configuration is bound in the host projects and passed into Core as a plain `CyteLlmOptions` object.
- AI analysis is optional and must stay optional. A token failure, a 401, an empty model list, an unknown model id, or an unreachable gateway must log a warning and let the run finish with workbook, CSVs and dashboard intact. Never fail a reconciliation because the gateway is down.
- Credentials never appear in a committed file. `appsettings.json` holds only `TokenUrl`, `Audience`, `ApiBaseUrl`.
- Exact config values, verbatim:
  - `TokenUrl`: `https://auth-qa.cyte.co.za/oauth/token`
  - `Audience`: `https://coreapi-qa.cyte.co.za/api/` (trailing slash is required)
  - `ApiBaseUrl`: `https://coreapi-qa.cyte.co.za/api` (no trailing slash)
- Token refresh margin is 5 minutes. A 401 triggers exactly one token refresh and one retry — never a loop.
- `HttpClient.Timeout` is 120 seconds. Observed gateway latency for an analysis-sized request is 23–27s.
- The existing 20 tests must stay green. They call `engine.Run(..., analyze: false)` and must not need editing.
- The gateway honours a `system` role message (verified). Send the existing system prompt as a `system` message; do not fold it into the user turn.
- Do not send `temperature` or `maxTokens`. The gateway applies its own defaults.

---

## Task 0: Put the repository under version control

This repo has no `.git` directory, so none of the existing work is tracked and the `git commit` steps in later tasks cannot run.

**If you or the user would rather not use git, skip this task and skip every "Commit" step in the rest of the plan.** Nothing else depends on it.

**Files:**
- Create: `.gitignore`

- [ ] **Step 1: Initialise the repository**

```bash
cd /c/Code/Cyte/hazard-rate-recon-dotnet
git init
```

- [ ] **Step 2: Create `.gitignore`**

```gitignore
bin/
obj/
.vs/
*.user
runs/
output/
```

- [ ] **Step 3: Commit the existing state as a baseline**

```bash
git add -A
git commit -m "chore: baseline existing reconciliation tool"
```

---

## Task 1: LLM contracts and token caching

**Files:**
- Create: `src/HazardRecon.Core/Llm/CyteLlmOptions.cs`
- Create: `src/HazardRecon.Core/Llm/LlmTypes.cs`
- Create: `src/HazardRecon.Core/Llm/ILlmClient.cs`
- Create: `src/HazardRecon.Core/Llm/LlmException.cs`
- Create: `src/HazardRecon.Core/Llm/CyteLlmClient.cs`
- Test: `tests/HazardRecon.Tests/Llm/FakeHttpMessageHandler.cs`
- Test: `tests/HazardRecon.Tests/Llm/CyteLlmClientTokenTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `CyteLlmOptions` with settable `string TokenUrl, Audience, ApiBaseUrl, ClientId, ClientSecret` and `bool IsConfigured { get; }`
  - `LlmModel { string Id; int Provider; string FriendlyName; string ModelName; }`
  - `LlmMessage { string Role; string Content; }` with `LlmMessage(string role, string content)`
  - `LlmChatResult { string Content; int InputTokens; int OutputTokens; }`
  - `LlmException : Exception` with `LlmException(string message)`
  - `interface ILlmClient { Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default); Task<LlmChatResult> ChatAsync(string modelId, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default); }`
  - `CyteLlmClient(CyteLlmOptions options, HttpMessageHandler? handler = null, Func<DateTime>? utcNow = null)` implementing `ILlmClient`
  - `FakeHttpMessageHandler(Func<HttpRequestMessage, int, (HttpStatusCode, string)> responder)` with `List<(string Method, string Url, string Body)> Requests { get; }`

- [ ] **Step 1: Write the failing tests**

Create `tests/HazardRecon.Tests/Llm/FakeHttpMessageHandler.cs`:

```csharp
using System.Net;
using System.Text;

namespace HazardRecon.Tests.Llm;

/// <summary>
/// Records every outbound request and answers from a caller-supplied responder.
/// The responder receives the request and its zero-based index, so a test can
/// return different responses on successive calls (e.g. 401 then 200).
/// </summary>
internal class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, (HttpStatusCode Status, string Body)> _responder;

    public List<(string Method, string Url, string Body)> Requests { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, int, (HttpStatusCode Status, string Body)> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
        int index = Requests.Count;
        Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));

        (HttpStatusCode status, string responseBody) = _responder(request, index);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
    }
}
```

Create `tests/HazardRecon.Tests/Llm/CyteLlmClientTokenTests.cs`:

```csharp
using System.Net;
using HazardRecon.Core.Llm;
using Xunit;

namespace HazardRecon.Tests.Llm;

public class CyteLlmClientTokenTests
{
    private const string ModelsJson = "[]";

    private static CyteLlmOptions Options() => new()
    {
        TokenUrl = "https://auth-qa.example/oauth/token",
        Audience = "https://api.example/api/",
        ApiBaseUrl = "https://api.example/api",
        ClientId = "id",
        ClientSecret = "secret"
    };

    private static string TokenJson(int expiresIn = 86400) =>
        $"{{\"access_token\":\"tok-{expiresIn}\",\"scope\":\"s\",\"expires_in\":{expiresIn},\"token_type\":\"Bearer\"}}";

    [Fact]
    public async Task TestTokenIsFetchedOnceForTwoCallsInsideValidityWindow()
    {
        DateTime now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
        FakeHttpMessageHandler handler = new((req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/oauth/token")
                ? (HttpStatusCode.OK, TokenJson())
                : (HttpStatusCode.OK, ModelsJson));

        CyteLlmClient client = new(Options(), handler, () => now);

        await client.ListModelsAsync();
        await client.ListModelsAsync();

        int tokenCalls = handler.Requests.Count(r => r.Url.EndsWith("/oauth/token"));
        Assert.Equal(1, tokenCalls);
        Assert.Equal(2, handler.Requests.Count(r => r.Url.EndsWith("/llm/models")));
    }

    [Fact]
    public async Task TestTokenIsRefetchedAfterItExpires()
    {
        DateTime now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
        FakeHttpMessageHandler handler = new((req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/oauth/token")
                ? (HttpStatusCode.OK, TokenJson(60))
                : (HttpStatusCode.OK, ModelsJson));

        CyteLlmClient client = new(Options(), handler, () => now);

        await client.ListModelsAsync();
        now = now.AddSeconds(120);          // past the 60s lifetime
        await client.ListModelsAsync();

        Assert.Equal(2, handler.Requests.Count(r => r.Url.EndsWith("/oauth/token")));
    }

    [Fact]
    public async Task TestTokenIsRefreshedEarlyInsideTheFiveMinuteMargin()
    {
        DateTime now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
        FakeHttpMessageHandler handler = new((req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/oauth/token")
                ? (HttpStatusCode.OK, TokenJson(600))
                : (HttpStatusCode.OK, ModelsJson));

        CyteLlmClient client = new(Options(), handler, () => now);

        await client.ListModelsAsync();
        now = now.AddSeconds(400);          // 200s left, inside the 300s margin
        await client.ListModelsAsync();

        Assert.Equal(2, handler.Requests.Count(r => r.Url.EndsWith("/oauth/token")));
    }

    [Fact]
    public async Task TestTokenRequestSendsClientCredentialsPayload()
    {
        DateTime now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
        FakeHttpMessageHandler handler = new((req, _) =>
            req.RequestUri!.AbsolutePath.EndsWith("/oauth/token")
                ? (HttpStatusCode.OK, TokenJson())
                : (HttpStatusCode.OK, ModelsJson));

        CyteLlmClient client = new(Options(), handler, () => now);
        await client.ListModelsAsync();

        string body = handler.Requests.First(r => r.Url.EndsWith("/oauth/token")).Body;
        Assert.Contains("\"client_id\":\"id\"", body);
        Assert.Contains("\"client_secret\":\"secret\"", body);
        Assert.Contains("\"audience\":\"https://api.example/api/\"", body);
        Assert.Contains("\"grant_type\":\"client_credentials\"", body);
    }

    [Fact]
    public async Task TestFailedTokenRequestThrowsLlmException()
    {
        DateTime now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.Forbidden, "{\"error\":\"nope\"}"));

        CyteLlmClient client = new(Options(), handler, () => now);

        LlmException ex = await Assert.ThrowsAsync<LlmException>(() => client.ListModelsAsync());
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public void TestIsConfiguredRequiresEveryValue()
    {
        Assert.True(Options().IsConfigured);

        CyteLlmOptions missingSecret = Options();
        missingSecret.ClientSecret = "";
        Assert.False(missingSecret.IsConfigured);

        CyteLlmOptions missingUrl = Options();
        missingUrl.TokenUrl = "   ";
        Assert.False(missingUrl.IsConfigured);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CyteLlmClientTokenTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'CyteLlmOptions' could not be found` (and the same for `CyteLlmClient`, `LlmException`).

- [ ] **Step 3: Create the option and type files**

`src/HazardRecon.Core/Llm/CyteLlmOptions.cs`:

```csharp
namespace HazardRecon.Core.Llm;

/// <summary>
/// Connection settings for the Cyte LLM gateway. Bound by the host projects and
/// passed into Core, so Core needs no configuration dependency of its own.
/// </summary>
public class CyteLlmOptions
{
    public string TokenUrl { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TokenUrl) &&
        !string.IsNullOrWhiteSpace(Audience) &&
        !string.IsNullOrWhiteSpace(ApiBaseUrl) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
```

`src/HazardRecon.Core/Llm/LlmTypes.cs`:

```csharp
namespace HazardRecon.Core.Llm;

public class LlmModel
{
    public string Id { get; set; } = string.Empty;
    public int Provider { get; set; }
    public string FriendlyName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
}

public class LlmMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    public LlmMessage() { }

    public LlmMessage(string role, string content)
    {
        Role = role;
        Content = content;
    }
}

public class LlmChatResult
{
    public string Content { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
```

`src/HazardRecon.Core/Llm/LlmException.cs`:

```csharp
namespace HazardRecon.Core.Llm;

public class LlmException : Exception
{
    public LlmException(string message) : base(message) { }
}
```

`src/HazardRecon.Core/Llm/ILlmClient.cs`:

```csharp
namespace HazardRecon.Core.Llm;

public interface ILlmClient
{
    Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default);

    Task<LlmChatResult> ChatAsync(string modelId, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default);
}
```

- [ ] **Step 4: Create `CyteLlmClient` with token caching and a stub `ChatAsync`**

`src/HazardRecon.Core/Llm/CyteLlmClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HazardRecon.Core.Llm;

/// <summary>
/// Talks to the Cyte LLM gateway. Owns the client-credentials token: it is cached
/// in memory for its stated lifetime, refreshed 5 minutes early, and refreshed once
/// more if the gateway rejects it with a 401.
/// </summary>
public class CyteLlmClient : ILlmClient, IDisposable
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly CyteLlmOptions _options;
    private readonly HttpClient _http;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _token;
    private DateTime _expiresAtUtc;

    public CyteLlmClient(CyteLlmOptions options, HttpMessageHandler? handler = null, Func<DateTime>? utcNow = null)
    {
        _options = options;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _http = handler != null ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    public Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<LlmChatResult> ChatAsync(string modelId, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default) =>
        throw new NotImplementedException();

    private async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token != null && _utcNow() < _expiresAtUtc - RefreshMargin)
            {
                return _token;
            }

            var body = new
            {
                client_id = _options.ClientId,
                client_secret = _options.ClientSecret,
                audience = _options.Audience,
                grant_type = "client_credentials"
            };

            using StringContent content = new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using HttpResponseMessage res = await _http.PostAsync(_options.TokenUrl, content, ct);
            string text = await res.Content.ReadAsStringAsync(ct);

            if (!res.IsSuccessStatusCode)
            {
                throw new LlmException($"token request failed: {(int)res.StatusCode} {res.ReasonPhrase}");
            }

            using JsonDocument doc = JsonDocument.Parse(text);
            string? token = doc.RootElement.TryGetProperty("access_token", out JsonElement t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(token))
            {
                throw new LlmException("token response contained no access_token");
            }

            int expiresIn = doc.RootElement.TryGetProperty("expires_in", out JsonElement e) && e.ValueKind == JsonValueKind.Number
                ? e.GetInt32()
                : 3600;

            _token = token;
            _expiresAtUtc = _utcNow().AddSeconds(expiresIn);
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Sends an authenticated request. On a 401 the cached token is discarded and the
    /// request is retried exactly once with a fresh one.
    /// </summary>
    private async Task<string> SendAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string token = await GetTokenAsync(forceRefresh: attempt > 0, ct);

            using HttpRequestMessage req = makeRequest();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using HttpResponseMessage res = await _http.SendAsync(req, ct);
            string text = await res.Content.ReadAsStringAsync(ct);

            if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                continue;
            }

            if (!res.IsSuccessStatusCode)
            {
                throw new LlmException($"{req.Method} {req.RequestUri} failed: {(int)res.StatusCode} {res.ReasonPhrase}");
            }

            return text;
        }

        throw new LlmException("authentication failed after refreshing the token");
    }

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }
}
```

- [ ] **Step 5: Implement `ListModelsAsync` so the token tests can exercise a real call**

Replace the `ListModelsAsync` stub in `CyteLlmClient.cs` with:

```csharp
    public async Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default)
    {
        string text = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiBaseUrl}/llm/models"), ct);

        List<LlmModel> models = new();
        using JsonDocument doc = JsonDocument.Parse(text);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (JsonElement m in doc.RootElement.EnumerateArray())
        {
            models.Add(new LlmModel
            {
                Id = m.TryGetProperty("id", out JsonElement i) ? i.GetString() ?? string.Empty : string.Empty,
                Provider = m.TryGetProperty("provider", out JsonElement p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
                FriendlyName = m.TryGetProperty("friendlyName", out JsonElement f) ? f.GetString() ?? string.Empty : string.Empty,
                ModelName = m.TryGetProperty("modelName", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty
            });
        }

        return models;
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CyteLlmClientTokenTests"`
Expected: PASS — 6 passed.

- [ ] **Step 7: Run the whole suite to confirm nothing regressed**

Run: `dotnet test`
Expected: PASS — 26 passed (20 existing + 6 new).

- [ ] **Step 8: Commit**

```bash
git add src/HazardRecon.Core/Llm tests/HazardRecon.Tests/Llm
git commit -m "feat: add Cyte LLM client with cached client-credentials token"
```

---

## Task 2: Model listing and 401 retry

**Files:**
- Modify: `src/HazardRecon.Core/Llm/CyteLlmClient.cs` (no signature changes; behaviour already written in Task 1 Step 5 — this task tests it and adds the retry coverage)
- Test: `tests/HazardRecon.Tests/Llm/CyteLlmClientModelsTests.cs`

**Interfaces:**
- Consumes: `CyteLlmClient`, `CyteLlmOptions`, `LlmModel`, `LlmException`, `FakeHttpMessageHandler` from Task 1.
- Produces: nothing new. Confirms `ListModelsAsync` parses the gateway payload and that the 401 retry works.

- [ ] **Step 1: Write the failing tests**

Create `tests/HazardRecon.Tests/Llm/CyteLlmClientModelsTests.cs`:

```csharp
using System.Net;
using HazardRecon.Core.Llm;
using Xunit;

namespace HazardRecon.Tests.Llm;

public class CyteLlmClientModelsTests
{
    private const string RealModelsJson = """
    [
      {
        "id": "72e110c8-e233-4486-bb3c-6dc3a56dca82",
        "provider": 1,
        "friendlyName": "Google Gemini 2.5 Pro",
        "modelName": "gemini-2.5-pro",
        "defaultParameters": "{\"temperature\":0.2}"
      },
      {
        "id": "5f3283d8-bc5d-44e5-8645-adf826d91939",
        "provider": 0,
        "friendlyName": "Azure OpenAI GPT-4o",
        "modelName": "gpt4o",
        "defaultParameters": "{\"temperature\":0.2}"
      }
    ]
    """;

    private static CyteLlmOptions Options() => new()
    {
        TokenUrl = "https://auth-qa.example/oauth/token",
        Audience = "https://api.example/api/",
        ApiBaseUrl = "https://api.example/api",
        ClientId = "id",
        ClientSecret = "secret"
    };

    private const string TokenJson = "{\"access_token\":\"tok\",\"expires_in\":86400,\"token_type\":\"Bearer\"}";

    private static bool IsToken(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.EndsWith("/oauth/token");

    [Fact]
    public async Task TestListModelsParsesTheGatewayPayload()
    {
        FakeHttpMessageHandler handler = new((req, _) =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.OK, RealModelsJson));

        CyteLlmClient client = new(Options(), handler);
        IReadOnlyList<LlmModel> models = await client.ListModelsAsync();

        Assert.Equal(2, models.Count);

        Assert.Equal("72e110c8-e233-4486-bb3c-6dc3a56dca82", models[0].Id);
        Assert.Equal(1, models[0].Provider);
        Assert.Equal("Google Gemini 2.5 Pro", models[0].FriendlyName);
        Assert.Equal("gemini-2.5-pro", models[0].ModelName);

        Assert.Equal("5f3283d8-bc5d-44e5-8645-adf826d91939", models[1].Id);
        Assert.Equal(0, models[1].Provider);
        Assert.Equal("Azure OpenAI GPT-4o", models[1].FriendlyName);
        Assert.Equal("gpt4o", models[1].ModelName);
    }

    [Fact]
    public async Task TestListModelsRequestsTheCorrectUrlWithABearerToken()
    {
        FakeHttpMessageHandler handler = new((req, _) =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.OK, "[]"));

        CyteLlmClient client = new(Options(), handler);
        await client.ListModelsAsync();

        Assert.Contains(handler.Requests, r => r.Method == "GET" && r.Url == "https://api.example/api/llm/models");
    }

    [Fact]
    public async Task TestSingle401TriggersOneTokenRefreshAndOneRetry()
    {
        // request 0: token, 1: models -> 401, 2: token again, 3: models -> 200
        FakeHttpMessageHandler handler = new((req, i) =>
        {
            if (IsToken(req)) return (HttpStatusCode.OK, TokenJson);
            return i == 1 ? (HttpStatusCode.Unauthorized, "{}") : (HttpStatusCode.OK, RealModelsJson);
        });

        CyteLlmClient client = new(Options(), handler);
        IReadOnlyList<LlmModel> models = await client.ListModelsAsync();

        Assert.Equal(2, models.Count);
        Assert.Equal(2, handler.Requests.Count(r => r.Url.EndsWith("/oauth/token")));
        Assert.Equal(2, handler.Requests.Count(r => r.Url.EndsWith("/llm/models")));
    }

    [Fact]
    public async Task TestTwoConsecutive401sFailWithoutLooping()
    {
        FakeHttpMessageHandler handler = new((req, _) =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.Unauthorized, "{}"));

        CyteLlmClient client = new(Options(), handler);

        await Assert.ThrowsAsync<LlmException>(() => client.ListModelsAsync());

        // exactly two attempts, not an unbounded retry loop
        Assert.Equal(2, handler.Requests.Count(r => r.Url.EndsWith("/llm/models")));
    }

    [Fact]
    public async Task TestNon401FailureIsNotRetried()
    {
        FakeHttpMessageHandler handler = new((req, _) =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.InternalServerError, "{}"));

        CyteLlmClient client = new(Options(), handler);

        LlmException ex = await Assert.ThrowsAsync<LlmException>(() => client.ListModelsAsync());
        Assert.Contains("500", ex.Message);
        Assert.Equal(1, handler.Requests.Count(r => r.Url.EndsWith("/llm/models")));
    }

    [Fact]
    public async Task TestEmptyArrayYieldsNoModels()
    {
        FakeHttpMessageHandler handler = new((req, _) =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.OK, "[]"));

        CyteLlmClient client = new(Options(), handler);
        Assert.Empty(await client.ListModelsAsync());
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test --filter "FullyQualifiedName~CyteLlmClientModelsTests"`
Expected: PASS — 6 passed. The implementation from Task 1 already satisfies these; if any fail, fix `CyteLlmClient` rather than the test.

- [ ] **Step 3: Commit**

```bash
git add tests/HazardRecon.Tests/Llm/CyteLlmClientModelsTests.cs
git commit -m "test: cover model listing and 401 retry behaviour"
```

---

## Task 3: Chat call

**Files:**
- Modify: `src/HazardRecon.Core/Llm/CyteLlmClient.cs` (replace the `ChatAsync` stub)
- Test: `tests/HazardRecon.Tests/Llm/CyteLlmClientChatTests.cs`

**Interfaces:**
- Consumes: everything from Task 1.
- Produces: working `CyteLlmClient.ChatAsync(string modelId, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)` returning `LlmChatResult`.

- [ ] **Step 1: Write the failing tests**

Create `tests/HazardRecon.Tests/Llm/CyteLlmClientChatTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using HazardRecon.Core.Llm;
using Xunit;

namespace HazardRecon.Tests.Llm;

public class CyteLlmClientChatTests
{
    private const string ModelId = "72e110c8-e233-4486-bb3c-6dc3a56dca82";
    private const string TokenJson = "{\"access_token\":\"tok\",\"expires_in\":86400,\"token_type\":\"Bearer\"}";
    private const string ChatJson = "{\"content\":\"Hello! How can I help you today?\",\"usage\":{\"inputTokens\":2,\"outputTokens\":9}}";

    private static CyteLlmOptions Options() => new()
    {
        TokenUrl = "https://auth-qa.example/oauth/token",
        Audience = "https://api.example/api/",
        ApiBaseUrl = "https://api.example/api",
        ClientId = "id",
        ClientSecret = "secret"
    };

    private static bool IsToken(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.EndsWith("/oauth/token");

    [Fact]
    public async Task TestChatPostsToTheModelSpecificUrl()
    {
        FakeHttpMessageHandler handler = new((req, _) =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.OK, ChatJson));

        CyteLlmClient client = new(Options(), handler);
        await client.ChatAsync(ModelId, new List<LlmMessage> { new("user", "Hi") });

        Assert.Contains(handler.Requests, r =>
            r.Method == "POST" &&
            r.Url == $"https://api.example/api/llm/models/{ModelId}/chat");
    }

    [Fact]
    public async Task TestChatSendsSystemThenUserInOrder()
    {
        FakeHttpMessageHandler handler = new((req, _) =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.OK, ChatJson));

        CyteLlmClient client = new(Options(), handler);
        await client.ChatAsync(ModelId, new List<LlmMessage>
        {
            new("system", "You are terse."),
            new("user", "Hi")
        });

        string body = handler.Requests.First(r => r.Url.EndsWith("/chat")).Body;

        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement messages = doc.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("You are terse.", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Hi", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TestChatDoesNotSendTemperatureOrMaxTokens()
    {
        FakeHttpMessageHandler handler = new((req, _) =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.OK, ChatJson));

        CyteLlmClient client = new(Options(), handler);
        await client.ChatAsync(ModelId, new List<LlmMessage> { new("user", "Hi") });

        string body = handler.Requests.First(r => r.Url.EndsWith("/chat")).Body;
        Assert.DoesNotContain("temperature", body);
        Assert.DoesNotContain("maxTokens", body);
    }

    [Fact]
    public async Task TestChatParsesContentAndUsage()
    {
        FakeHttpMessageHandler handler = new((req, _) =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.OK, ChatJson));

        CyteLlmClient client = new(Options(), handler);
        LlmChatResult result = await client.ChatAsync(ModelId, new List<LlmMessage> { new("user", "Hi") });

        Assert.Equal("Hello! How can I help you today?", result.Content);
        Assert.Equal(2, result.InputTokens);
        Assert.Equal(9, result.OutputTokens);
    }

    [Fact]
    public async Task TestChatToleratesAMissingUsageBlock()
    {
        FakeHttpMessageHandler handler = new((req, _) =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.OK, "{\"content\":\"ok\"}"));

        CyteLlmClient client = new(Options(), handler);
        LlmChatResult result = await client.ChatAsync(ModelId, new List<LlmMessage> { new("user", "Hi") });

        Assert.Equal("ok", result.Content);
        Assert.Equal(0, result.InputTokens);
        Assert.Equal(0, result.OutputTokens);
    }

    [Fact]
    public async Task TestUnknownModelIdSurfacesAs404()
    {
        FakeHttpMessageHandler handler = new((req, _) =>
            IsToken(req) ? (HttpStatusCode.OK, TokenJson) : (HttpStatusCode.NotFound, "{}"));

        CyteLlmClient client = new(Options(), handler);

        LlmException ex = await Assert.ThrowsAsync<LlmException>(
            () => client.ChatAsync("00000000-0000-0000-0000-000000000000",
                                   new List<LlmMessage> { new("user", "Hi") }));
        Assert.Contains("404", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CyteLlmClientChatTests"`
Expected: FAIL — 6 failed with `System.NotImplementedException`.

- [ ] **Step 3: Implement `ChatAsync`**

In `src/HazardRecon.Core/Llm/CyteLlmClient.cs`, replace the `ChatAsync` stub with:

```csharp
    public async Task<LlmChatResult> ChatAsync(string modelId, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)
    {
        var payload = new
        {
            messages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray()
        };
        string json = JsonSerializer.Serialize(payload);
        string url = $"{_options.ApiBaseUrl}/llm/models/{modelId}/chat";

        string text = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        }, ct);

        using JsonDocument doc = JsonDocument.Parse(text);
        LlmChatResult result = new()
        {
            Content = doc.RootElement.TryGetProperty("content", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty
        };

        if (doc.RootElement.TryGetProperty("usage", out JsonElement u) && u.ValueKind == JsonValueKind.Object)
        {
            result.InputTokens = u.TryGetProperty("inputTokens", out JsonElement it) && it.ValueKind == JsonValueKind.Number ? it.GetInt32() : 0;
            result.OutputTokens = u.TryGetProperty("outputTokens", out JsonElement ot) && ot.ValueKind == JsonValueKind.Number ? ot.GetInt32() : 0;
        }

        return result;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CyteLlmClientChatTests"`
Expected: PASS — 6 passed.

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Core/Llm/CyteLlmClient.cs tests/HazardRecon.Tests/Llm/CyteLlmClientChatTests.cs
git commit -m "feat: add chat call to the Cyte LLM client"
```

---

## Task 4: Point the analysis at the gateway and wire it through the engine

Changing `AiAnalysisService` breaks `ReconciliationEngine`, which breaks the CLI and Web. All of that is fixed in this task so the build stays green at the task boundary.

**Files:**
- Create: `src/HazardRecon.Core/Helpers/MarkdownHelper.cs`
- Modify: `src/HazardRecon.Core/Services/AiAnalysisService.cs` (whole file)
- Modify: `src/HazardRecon.Core/Exporters/DashboardRenderer.cs` (delete the private `SimpleMarkdownToHtml`, call the shared helper)
- Modify: `src/HazardRecon.Core/Services/ReconciliationEngine.cs:157` (signature) and the analysis block
- Modify: `src/HazardRecon.Cli/Program.cs:51` (call site only — the `--model` flag comes in Task 8)
- Modify: `src/HazardRecon.Web/Program.cs:138` (call site only — full wiring comes in Task 7)
- Test: `tests/HazardRecon.Tests/Llm/FakeLlmClient.cs`
- Test: `tests/HazardRecon.Tests/AiAnalysisServiceTests.cs`
- Test: `tests/HazardRecon.Tests/EngineAnalysisTests.cs`

**Interfaces:**
- Consumes: `ILlmClient`, `LlmMessage`, `LlmChatResult`, `LlmException` from Task 1.
- Produces:
  - `MarkdownHelper.ToHtml(string md)` → `string` (public static)
  - `AiAnalysisService(ILlmClient client, string modelId)` instance ctor
  - `AiAnalysisService.BuildAnalysisPayload(Dictionary<string, SingleSetResult> results)` stays `public static`
  - `AiAnalysisService.GenerateAnalysis(Dictionary<string, object> payload, Action<string, string>? log = null)` → `string?` (now an instance method)
  - `ReconciliationEngine.Run(object root, string outdir = "output", Action<string, string>? logger = null, bool analyze = false, AiAnalysisService? analyst = null)`
  - `FakeLlmClient` test double with `string? LastModelId`, `List<LlmMessage> LastMessages`, settable `string ReplyContent`, `Exception? ThrowOnChat`, `List<LlmModel> Models`

- [ ] **Step 1: Write the failing tests**

Create `tests/HazardRecon.Tests/Llm/FakeLlmClient.cs`:

```csharp
using HazardRecon.Core.Llm;

namespace HazardRecon.Tests.Llm;

internal class FakeLlmClient : ILlmClient
{
    public List<LlmModel> Models { get; set; } = new();
    public string ReplyContent { get; set; } = "## Executive summary\n\nAll tied out.";
    public Exception? ThrowOnChat { get; set; }

    public string? LastModelId { get; private set; }
    public List<LlmMessage> LastMessages { get; private set; } = new();
    public int ChatCalls { get; private set; }

    public Task<IReadOnlyList<LlmModel>> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LlmModel>>(Models);

    public Task<LlmChatResult> ChatAsync(string modelId, IReadOnlyList<LlmMessage> messages, CancellationToken ct = default)
    {
        ChatCalls++;
        LastModelId = modelId;
        LastMessages = messages.ToList();

        if (ThrowOnChat != null)
        {
            return Task.FromException<LlmChatResult>(ThrowOnChat);
        }

        return Task.FromResult(new LlmChatResult { Content = ReplyContent, InputTokens = 10, OutputTokens = 20 });
    }
}
```

Create `tests/HazardRecon.Tests/AiAnalysisServiceTests.cs`:

```csharp
using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using HazardRecon.Tests.Llm;
using Xunit;

namespace HazardRecon.Tests;

public class AiAnalysisServiceTests
{
    private const string ModelId = "72e110c8-e233-4486-bb3c-6dc3a56dca82";

    private static Dictionary<string, object> Payload() =>
        AiAnalysisService.BuildAnalysisPayload(new Dictionary<string, SingleSetResult>
        {
            ["JUN2026"] = new SingleSetResult
            {
                Summary = new ReconciliationSummary { Label = "set", TotalDefaults = 3, UntracedTotal = 1 }
            }
        });

    [Fact]
    public void TestReturnsTheModelsMarkdown()
    {
        FakeLlmClient client = new() { ReplyContent = "## Executive summary\n\nClean." };
        AiAnalysisService service = new(client, ModelId);

        string? md = service.GenerateAnalysis(Payload());

        Assert.Equal("## Executive summary\n\nClean.", md);
        Assert.Equal(ModelId, client.LastModelId);
    }

    [Fact]
    public void TestSendsTheSystemPromptAsASystemMessage()
    {
        FakeLlmClient client = new();
        AiAnalysisService service = new(client, ModelId);

        service.GenerateAnalysis(Payload());

        Assert.Equal(2, client.LastMessages.Count);
        Assert.Equal("system", client.LastMessages[0].Role);
        Assert.Contains("senior credit-risk analyst", client.LastMessages[0].Content);
        Assert.Equal("user", client.LastMessages[1].Role);
        Assert.Contains("Aggregate reconciliation results", client.LastMessages[1].Content);
        Assert.Contains("JUN2026", client.LastMessages[1].Content);
    }

    [Fact]
    public void TestReturnsNullAndWarnsWhenTheGatewayFails()
    {
        FakeLlmClient client = new() { ThrowOnChat = new LlmException("token request failed: 403 Forbidden") };
        AiAnalysisService service = new(client, ModelId);

        List<(string Msg, string Kind)> log = new();
        string? md = service.GenerateAnalysis(Payload(), (m, k) => log.Add((m, k)));

        Assert.Null(md);
        Assert.Contains(log, l => l.Kind == "warn" && l.Msg.Contains("403"));
    }

    [Fact]
    public void TestReturnsNullWhenTheModelRepliesWithNothing()
    {
        FakeLlmClient client = new() { ReplyContent = "   " };
        AiAnalysisService service = new(client, ModelId);

        List<(string Msg, string Kind)> log = new();
        Assert.Null(service.GenerateAnalysis(Payload(), (m, k) => log.Add((m, k))));
        Assert.Contains(log, l => l.Kind == "warn");
    }
}
```

Create `tests/HazardRecon.Tests/EngineAnalysisTests.cs`:

```csharp
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class EngineAnalysisTests : IClassFixture<SyntheticDataFixture>
{
    private readonly SyntheticDataFixture _fixture;

    public EngineAnalysisTests(SyntheticDataFixture fixture) => _fixture = fixture;

    [Fact]
    public void TestAnalyzeWithoutAnAnalystCompletesTheRunAndLogsTheSkip()
    {
        ReconciliationEngine engine = new();
        List<(string Msg, string Kind)> log = new();

        ReconciliationRunResult result = engine.Run(
            _fixture.RootDir,
            Path.Combine(_fixture.OutDir, "no-analyst"),
            logger: (m, k) => log.Add((m, k)),
            analyze: true,
            analyst: null);

        Assert.Null(result.Analysis);
        Assert.Null(result.Memo);
        Assert.NotEmpty(result.Workbook);
        Assert.NotEmpty(result.Dashboard);
        Assert.Contains(log, l => l.Kind == "warn" && l.Msg.Contains("no model selected"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~AiAnalysisServiceTests|FullyQualifiedName~EngineAnalysisTests"`
Expected: build FAILS — `CS1729: 'AiAnalysisService' does not contain a constructor that takes 2 arguments` and `CS1739: The best overload for 'Run' does not have a parameter named 'analyst'`.

- [ ] **Step 3: Create the shared markdown helper**

`src/HazardRecon.Core/Helpers/MarkdownHelper.cs`:

```csharp
using System.Net;
using System.Text;

namespace HazardRecon.Core.Helpers;

/// <summary>
/// The small subset of Markdown the model is asked to produce: h2/h3 headings,
/// bullet items and paragraphs. Shared by the dashboard and the chat reply so both
/// render generated text the same way.
/// </summary>
public static class MarkdownHelper
{
    public static string ToHtml(string? md)
    {
        if (string.IsNullOrWhiteSpace(md)) return string.Empty;

        StringBuilder sb = new();
        foreach (string line in md.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("## ")) sb.AppendLine($"<h3>{WebUtility.HtmlEncode(t[3..])}</h3>");
            else if (t.StartsWith("# ")) sb.AppendLine($"<h2>{WebUtility.HtmlEncode(t[2..])}</h2>");
            else if (t.StartsWith("- ") || t.StartsWith("* ")) sb.AppendLine($"<li>{WebUtility.HtmlEncode(t[2..])}</li>");
            else if (!string.IsNullOrEmpty(t)) sb.AppendLine($"<p>{WebUtility.HtmlEncode(t)}</p>");
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Point the dashboard at the shared helper**

In `src/HazardRecon.Core/Exporters/DashboardRenderer.cs`:

1. Delete the entire `private static string SimpleMarkdownToHtml(string md)` method.
2. Change the one call site from `SimpleMarkdownToHtml(analysisMd)` to `MarkdownHelper.ToHtml(analysisMd)`.

`using HazardRecon.Core.Helpers;` is already present at the top of that file, so no using change is needed.

- [ ] **Step 5: Rewrite `AiAnalysisService`**

Replace the whole of `src/HazardRecon.Core/Services/AiAnalysisService.cs` with:

```csharp
using System.Text.Json;
using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;

namespace HazardRecon.Core.Services;

public class AiAnalysisService
{
    private const string SystemPrompt = @"You are a senior credit-risk analyst at Anchor Point Risk writing
for a bank's finance and audit teams. You receive aggregate results of an
IFRS 9 hazard-rate reconciliation as JSON. Write a rigorous, plain-language
analysis in Markdown with exactly these sections:

## Executive summary
## Check 1 - default traceability
## Check 2 - write-offs never flagged as default
## Bucket migrations
## Data quality flags
## Recommended actions

Rules: report numbers exactly as given (thousands separators; rand amounts
as R1,234,567.89). Call out root-cause patterns the aggregates support -
e.g. a concentration of in-window exceptions by last-scored bucket. When
more than one set is present, compare them. Flag anomalies (zero IFRS9
overlap, missing files, empty windows). Never invent numbers that are not
in the input. Use headings, short paragraphs and bullet lists only - no
Markdown tables. Keep it under 700 words.";

    private readonly ILlmClient _client;
    private readonly string _modelId;

    public AiAnalysisService(ILlmClient client, string modelId)
    {
        _client = client;
        _modelId = modelId;
    }

    public static Dictionary<string, object> BuildAnalysisPayload(Dictionary<string, SingleSetResult> results)
    {
        List<Dictionary<string, object?>> sets = new();

        foreach (var (key, r) in results)
        {
            ReconciliationSummary s = r.Summary;

            Dictionary<string, int> hist = new();
            if (r.WoNd != null && r.WoNd.Count > 0)
            {
                var inw = r.WoNd.Where(w => w.WriteOffVsScoringWindow == "IN WINDOW");
                hist = inw
                    .GroupBy(w => w.LastBucketRating ?? "unknown")
                    .ToDictionary(g => g.Key, g => g.Count());
            }

            Dictionary<string, object>? matrix = null;
            if (r.Mig.RawCounts.Count > 0)
            {
                int[,] m = MigrationMatrixBuilder.MatrixForPeriod(r.Mig);
                List<List<int>> countsList = new();
                for (int i = 0; i < 6; i++)
                {
                    List<int> row = new();
                    for (int j = 0; j < 6; j++) row.Add(m[i, j]);
                    countsList.Add(row);
                }

                matrix = new Dictionary<string, object>
                {
                    ["buckets"] = new List<int> { 1, 2, 3, 4, 5, 6 },
                    ["from_to_counts"] = countsList
                };
            }

            sets.Add(new Dictionary<string, object?>
            {
                ["key"] = key,
                ["label"] = s.Label,
                ["window"] = s.Window,
                ["defaults"] = s.TotalDefaults,
                ["default_exposure"] = s.TotalExposure,
                ["traced_writeoff"] = s.TracedWriteOff,
                ["traced_ifrs9"] = s.TracedIfrs9,
                ["untraced"] = s.UntracedTotal,
                ["untraced_exposure"] = s.UntracedExposure,
                ["untraced_fully_recovered"] = s.UntracedFullyRecovered,
                ["untraced_fully_recovered_amount"] = s.UntracedFullyRecoveredAmount,
                ["trace_rate"] = s.TraceRate,
                ["check2_total"] = s.WoNotDefaultTotal,
                ["check2_in_window"] = s.WoInWindow,
                ["check2_in_window_amount"] = s.WoInWindowAmount,
                ["check2_post_window"] = s.WoPostWindow,
                ["check2_pre_window"] = s.WoPreWindow,
                ["in_window_last_bucket_hist"] = hist,
                ["scored_distinct"] = s.ScoredDistinct,
                ["writeoff_distinct"] = s.WriteOffDistinct,
                ["ifrs9_distinct"] = s.Ifrs9Distinct,
                ["ifrs9_key_overlap"] = s.Ifrs9KeyOverlap,
                ["migration_matrix"] = matrix,
                ["migration_validation"] = s.MigValidation,
                ["engine_params"] = r.Engine.Params
            });
        }

        return new Dictionary<string, object> { ["sets"] = sets };
    }

    /// <summary>
    /// Blocks on the async client because the engine is synchronous and already runs
    /// on a background thread. Returns null on any failure — a gateway outage must
    /// never fail a reconciliation.
    /// </summary>
    public string? GenerateAnalysis(Dictionary<string, object> payload, Action<string, string>? log = null)
    {
        try
        {
            string jsonPayload = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

            List<LlmMessage> messages = new()
            {
                new LlmMessage("system", SystemPrompt),
                new LlmMessage("user", $"Aggregate reconciliation results:\n\n{jsonPayload}")
            };

            LlmChatResult res = _client.ChatAsync(_modelId, messages).GetAwaiter().GetResult();
            string result = (res.Content ?? string.Empty).Trim();

            if (string.IsNullOrEmpty(result))
            {
                log?.Invoke("AI analysis returned no content", "warn");
                return null;
            }

            log?.Invoke($"AI analysis generated ({res.OutputTokens:N0} output tokens)", "ok");
            return result;
        }
        catch (Exception ex)
        {
            log?.Invoke($"AI analysis unavailable: {ex.GetType().Name}: {ex.Message}", "warn");
            return null;
        }
    }
}
```

- [ ] **Step 6: Give the engine an optional analyst**

In `src/HazardRecon.Core/Services/ReconciliationEngine.cs`, change the `Run` signature to:

```csharp
    public ReconciliationRunResult Run(object root, string outdir = "output", Action<string, string>? logger = null, bool analyze = false, AiAnalysisService? analyst = null)
```

and replace the analysis block with:

```csharp
        string? analysisMd = null;
        if (analyze)
        {
            if (analyst == null)
            {
                log("no model selected - skipping AI analysis", "warn");
            }
            else
            {
                log("Generating AI analysis", "head");
                var payload = AiAnalysisService.BuildAnalysisPayload(results);
                analysisMd = analyst.GenerateAnalysis(payload, log);
            }
        }
```

- [ ] **Step 7: Keep the two call sites compiling**

`src/HazardRecon.Cli/Program.cs` — replace the `engine.Run(...)` line with:

```csharp
            ReconciliationRunResult result = engine.Run(roots, outdir, analyze: false);
```

`src/HazardRecon.Web/Program.cs` — replace the `engine.Run(...)` line with:

```csharp
            ReconciliationRunResult outResult = engine.Run(capturedJob.Roots, capturedJob.Outdir, logger: Logger, analyze: false);
```

Both are temporary: Task 7 restores analysis in Web, Task 8 in the CLI. Analysis is off in between, which is why the next step runs the whole suite rather than only the new tests.

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 43 passed (38 from Tasks 1–3, plus 4 analysis tests and 1 engine test).

- [ ] **Step 9: Commit**

```bash
git add src tests
git commit -m "feat: run AI analysis through ILlmClient instead of the Anthropic API"
```

---

## Task 5: Make the chat actually call a model

**Files:**
- Modify: `src/HazardRecon.Core/Services/ChatService.cs` (whole file)
- Modify: `src/HazardRecon.Web/Program.cs:218` (call site only — full wiring in Task 7)
- Test: `tests/HazardRecon.Tests/ChatServiceTests.cs`

**Interfaces:**
- Consumes: `ILlmClient`, `LlmMessage`, `LlmException` (Task 1); `MarkdownHelper.ToHtml` (Task 4); `FakeLlmClient` (Task 4).
- Produces:
  - `ChatService(ILlmClient? client, string? modelId)` instance ctor
  - `ChatService.ProcessQuestion(string userQuestion, Dictionary<string, object> runAggregates)` → `ChatService.ChatResponse` (now an instance method)
  - `ChatService.ChatResponse { string Reply; string ReplyHtml; bool IsError; string? ErrorMessage; }` — unchanged shape

- [ ] **Step 1: Write the failing tests**

Create `tests/HazardRecon.Tests/ChatServiceTests.cs`:

```csharp
using HazardRecon.Core.Llm;
using HazardRecon.Core.Services;
using HazardRecon.Tests.Llm;
using Xunit;

namespace HazardRecon.Tests;

public class ChatServiceTests
{
    private const string ModelId = "72e110c8-e233-4486-bb3c-6dc3a56dca82";

    private static Dictionary<string, object> Aggregates() =>
        new() { ["sets"] = new List<Dictionary<string, object?>> { new() { ["key"] = "JUN2026", ["untraced"] = 373 } } };

    [Fact]
    public void TestErrorsWhenNoModelWasSelected()
    {
        ChatService service = new(new FakeLlmClient(), null);

        ChatService.ChatResponse res = service.ProcessQuestion("How many untraced?", Aggregates());

        Assert.True(res.IsError);
        Assert.Equal("No model was selected for this run.", res.ErrorMessage);
    }

    [Fact]
    public void TestErrorsWhenThereIsNoClient()
    {
        ChatService service = new(null, ModelId);

        ChatService.ChatResponse res = service.ProcessQuestion("How many untraced?", Aggregates());

        Assert.True(res.IsError);
        Assert.NotNull(res.ErrorMessage);
    }

    [Fact]
    public void TestReturnsTheModelsAnswerAsTextAndHtml()
    {
        FakeLlmClient client = new() { ReplyContent = "There were 373 untraced defaults." };
        ChatService service = new(client, ModelId);

        ChatService.ChatResponse res = service.ProcessQuestion("How many untraced?", Aggregates());

        Assert.False(res.IsError);
        Assert.Equal("There were 373 untraced defaults.", res.Reply);
        Assert.Contains("<p>There were 373 untraced defaults.</p>", res.ReplyHtml);
        Assert.Equal(ModelId, client.LastModelId);
    }

    [Fact]
    public void TestSendsTheAggregatesAndTheQuestion()
    {
        FakeLlmClient client = new();
        ChatService service = new(client, ModelId);

        service.ProcessQuestion("Why is IFRS9 zero?", Aggregates());

        Assert.Equal(2, client.LastMessages.Count);
        Assert.Equal("system", client.LastMessages[0].Role);
        Assert.Equal("user", client.LastMessages[1].Role);
        Assert.Contains("JUN2026", client.LastMessages[1].Content);
        Assert.Contains("Why is IFRS9 zero?", client.LastMessages[1].Content);
    }

    [Fact]
    public void TestGatewayFailureBecomesAnErrorResponse()
    {
        FakeLlmClient client = new() { ThrowOnChat = new LlmException("POST .../chat failed: 500 Internal Server Error") };
        ChatService service = new(client, ModelId);

        ChatService.ChatResponse res = service.ProcessQuestion("Anything?", Aggregates());

        Assert.True(res.IsError);
        Assert.Contains("500", res.ErrorMessage);
    }

    [Fact]
    public void TestEmptyAnswerBecomesAnErrorResponse()
    {
        FakeLlmClient client = new() { ReplyContent = "  " };
        ChatService service = new(client, ModelId);

        ChatService.ChatResponse res = service.ProcessQuestion("Anything?", Aggregates());

        Assert.True(res.IsError);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ChatServiceTests"`
Expected: build FAILS — `CS1729: 'ChatService' does not contain a constructor that takes 2 arguments`.

- [ ] **Step 3: Rewrite `ChatService`**

Replace the whole of `src/HazardRecon.Core/Services/ChatService.cs` with:

```csharp
using System.Text.Json;
using HazardRecon.Core.Helpers;
using HazardRecon.Core.Llm;

namespace HazardRecon.Core.Services;

/// <summary>
/// "Ask about this run". Only the run's aggregate figures are sent to the model —
/// no account-level rows leave this machine, which is why no account masking is
/// needed on this path. The trade-off is that the model can answer about totals and
/// rates but not about individual accounts.
/// </summary>
public class ChatService
{
    private const string SystemPrompt = @"You are a credit-risk analyst answering questions about one
IFRS 9 hazard-rate reconciliation run. You are given the run's aggregate results as
JSON, then a question. Answer only from those figures. Report numbers exactly as
given (thousands separators; rand amounts as R1,234,567.89). If the figures do not
contain the answer, say so plainly and say what would. You have aggregates only, not
account-level data, so you cannot answer questions about individual accounts. Keep
the answer short — a few sentences or a short bullet list. Markdown, no tables.";

    private readonly ILlmClient? _client;
    private readonly string? _modelId;

    public ChatService(ILlmClient? client, string? modelId)
    {
        _client = client;
        _modelId = modelId;
    }

    public class ChatResponse
    {
        public string Reply { get; set; } = string.Empty;
        public string ReplyHtml { get; set; } = string.Empty;
        public bool IsError { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public ChatResponse ProcessQuestion(string userQuestion, Dictionary<string, object> runAggregates)
    {
        if (string.IsNullOrEmpty(_modelId))
        {
            return new ChatResponse { IsError = true, ErrorMessage = "No model was selected for this run." };
        }

        if (_client == null)
        {
            return new ChatResponse { IsError = true, ErrorMessage = "Chat is unavailable - the LLM gateway is not configured." };
        }

        try
        {
            string json = JsonSerializer.Serialize(runAggregates, new JsonSerializerOptions { WriteIndented = true });

            List<LlmMessage> messages = new()
            {
                new LlmMessage("system", SystemPrompt),
                new LlmMessage("user", $"Reconciliation results:\n\n{json}\n\nQuestion: {userQuestion}")
            };

            LlmChatResult res = _client.ChatAsync(_modelId, messages).GetAwaiter().GetResult();
            string reply = (res.Content ?? string.Empty).Trim();

            if (reply.Length == 0)
            {
                return new ChatResponse { IsError = true, ErrorMessage = "The model returned an empty answer." };
            }

            return new ChatResponse { Reply = reply, ReplyHtml = MarkdownHelper.ToHtml(reply) };
        }
        catch (Exception ex)
        {
            return new ChatResponse { IsError = true, ErrorMessage = $"Chat is unavailable - {ex.Message}" };
        }
    }
}
```

The pre-existing `MaskValue` and `MaskAccountsInText` helpers are **deleted**, along
with the `AccountRegex` field and the `System.Text.RegularExpressions` using. They
were never called, and this path sends aggregates only — no account numbers leave the
machine, so there is nothing to mask. If per-account chat is built later it will need
its own masking against the account set it actually ships; recover these from git
history then rather than carrying untested dead code now.

- [ ] **Step 4: Keep the Web call site compiling**

In `src/HazardRecon.Web/Program.cs`, replace:

```csharp
    var chatRes = ChatService.ProcessQuestion(message, new Dictionary<string, object>());
```

with:

```csharp
    ChatService chatService = new(null, null);
    var chatRes = chatService.ProcessQuestion(message, new Dictionary<string, object>());
```

Temporary — Task 7 passes the real client, model id and aggregates.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test`
Expected: PASS — 49 passed.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: answer run questions with the selected model"
```

---

## Task 6: Model resolution helper

Needed by both the CLI (`--model`) and, for consistency, any future caller. Split out so its rules are tested without a running gateway.

**Files:**
- Create: `src/HazardRecon.Core/Llm/ModelResolver.cs`
- Test: `tests/HazardRecon.Tests/Llm/ModelResolverTests.cs`

**Interfaces:**
- Consumes: `LlmModel` (Task 1).
- Produces: `ModelResolver.Resolve(IReadOnlyList<LlmModel> models, string? fragment)` → `LlmModel?`

- [ ] **Step 1: Write the failing tests**

Create `tests/HazardRecon.Tests/Llm/ModelResolverTests.cs`:

```csharp
using HazardRecon.Core.Llm;
using Xunit;

namespace HazardRecon.Tests.Llm;

public class ModelResolverTests
{
    private static List<LlmModel> Models() => new()
    {
        new LlmModel { Id = "72e110c8-e233-4486-bb3c-6dc3a56dca82", Provider = 1, FriendlyName = "Google Gemini 2.5 Pro", ModelName = "gemini-2.5-pro" },
        new LlmModel { Id = "5f3283d8-bc5d-44e5-8645-adf826d91939", Provider = 0, FriendlyName = "Azure OpenAI GPT-4o", ModelName = "gpt4o" }
    };

    [Fact]
    public void TestNoFragmentPicksTheFirstModel()
    {
        Assert.Equal("72e110c8-e233-4486-bb3c-6dc3a56dca82", ModelResolver.Resolve(Models(), null)!.Id);
        Assert.Equal("72e110c8-e233-4486-bb3c-6dc3a56dca82", ModelResolver.Resolve(Models(), "   ")!.Id);
    }

    [Fact]
    public void TestExactIdMatches()
    {
        Assert.Equal("Azure OpenAI GPT-4o",
            ModelResolver.Resolve(Models(), "5f3283d8-bc5d-44e5-8645-adf826d91939")!.FriendlyName);
    }

    [Fact]
    public void TestFriendlyNameFragmentMatchesCaseInsensitively()
    {
        Assert.Equal("Azure OpenAI GPT-4o", ModelResolver.Resolve(Models(), "gpt-4o")!.FriendlyName);
        Assert.Equal("Google Gemini 2.5 Pro", ModelResolver.Resolve(Models(), "GEMINI")!.FriendlyName);
    }

    [Fact]
    public void TestModelNameFragmentMatches()
    {
        Assert.Equal("Azure OpenAI GPT-4o", ModelResolver.Resolve(Models(), "gpt4o")!.FriendlyName);
    }

    [Fact]
    public void TestAmbiguousFragmentTakesTheFirstInGatewayOrder()
    {
        // "o" appears in both friendly names; first wins rather than erroring
        Assert.Equal("Google Gemini 2.5 Pro", ModelResolver.Resolve(Models(), "o")!.FriendlyName);
    }

    [Fact]
    public void TestUnmatchedFragmentReturnsNull()
    {
        Assert.Null(ModelResolver.Resolve(Models(), "llama"));
    }

    [Fact]
    public void TestEmptyModelListReturnsNull()
    {
        Assert.Null(ModelResolver.Resolve(new List<LlmModel>(), null));
        Assert.Null(ModelResolver.Resolve(new List<LlmModel>(), "gemini"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ModelResolverTests"`
Expected: build FAILS — `CS0103: The name 'ModelResolver' does not exist`.

- [ ] **Step 3: Implement `ModelResolver`**

`src/HazardRecon.Core/Llm/ModelResolver.cs`:

```csharp
namespace HazardRecon.Core.Llm;

public static class ModelResolver
{
    /// <summary>
    /// Resolves a user-supplied fragment to one model. An empty fragment means "the
    /// first model the gateway offered". A fragment matches an exact id, or appears
    /// anywhere in the friendly name or model name, case-insensitively. When several
    /// match, the first in gateway order wins — ambiguity is not an error. Returns
    /// null when nothing matches or there are no models.
    /// </summary>
    public static LlmModel? Resolve(IReadOnlyList<LlmModel> models, string? fragment)
    {
        if (models.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(fragment)) return models[0];

        string f = fragment.Trim();
        return models.FirstOrDefault(m =>
            m.Id.Equals(f, StringComparison.OrdinalIgnoreCase) ||
            m.FriendlyName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
            m.ModelName.Contains(f, StringComparison.OrdinalIgnoreCase));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ModelResolverTests"`
Expected: PASS — 7 passed.

- [ ] **Step 5: Commit**

```bash
git add src/HazardRecon.Core/Llm/ModelResolver.cs tests/HazardRecon.Tests/Llm/ModelResolverTests.cs
git commit -m "feat: add model fragment resolution"
```

---

## Task 7: Wire the gateway into the web app

**Files:**
- Modify: `src/HazardRecon.Web/HazardRecon.Web.csproj` (add `UserSecretsId`)
- Modify: `src/HazardRecon.Web/appsettings.json` (add the `CyteLlm` section)
- Modify: `src/HazardRecon.Web/JobState.cs` (add `ModelId`, `AnalysisPayload`)
- Modify: `src/HazardRecon.Web/Program.cs` (options binding, `GET /api/models`, `model_id` on run, real chat)

**Interfaces:**
- Consumes: `CyteLlmOptions`, `CyteLlmClient` (Task 1); `AiAnalysisService(ILlmClient, string)` and `ReconciliationEngine.Run(..., analyst:)` (Task 4); `ChatService(ILlmClient?, string?)` (Task 5).
- Produces: `GET /api/models` returning `[{ id, provider, friendlyName, modelName }]`; `POST /api/run` accepting `model_id`.

- [ ] **Step 1: Add the user-secrets id to the Web project**

In `src/HazardRecon.Web/HazardRecon.Web.csproj`, inside the existing `<PropertyGroup>`, add:

```xml
    <UserSecretsId>hazard-recon-cyte-llm</UserSecretsId>
```

- [ ] **Step 2: Add the non-secret configuration**

In `src/HazardRecon.Web/appsettings.json`, add a `CyteLlm` section alongside `Logging` and `AllowedHosts`:

```json
  "CyteLlm": {
    "TokenUrl": "https://auth-qa.cyte.co.za/oauth/token",
    "Audience": "https://coreapi-qa.cyte.co.za/api/",
    "ApiBaseUrl": "https://coreapi-qa.cyte.co.za/api"
  }
```

- [ ] **Step 3: Store the secrets locally**

Run, substituting the values supplied separately (they are NOT in this plan by design):

```bash
cd /c/Code/Cyte/hazard-rate-recon-dotnet/src/HazardRecon.Web
dotnet user-secrets set "CyteLlm:ClientId" "<client id>"
dotnet user-secrets set "CyteLlm:ClientSecret" "<client secret>"
dotnet user-secrets list
```

Expected: `dotnet user-secrets list` prints both keys.

- [ ] **Step 4: Extend `JobState`**

In `src/HazardRecon.Web/JobState.cs`, add two properties to the class:

```csharp
    public string? ModelId { get; set; }
    public Dictionary<string, object>? AnalysisPayload { get; set; }
```

`AnalysisPayload` is populated after every run, whether or not analysis ran, so the chat has figures to answer from.

- [ ] **Step 5: Bind options and construct the client**

In `src/HazardRecon.Web/Program.cs`, after `var app = builder.Build();` and the existing `app.UseDefaultFiles(); app.UseStaticFiles();` lines, add:

```csharp
CyteLlmOptions llmOptions = new();
builder.Configuration.GetSection("CyteLlm").Bind(llmOptions);
CyteLlmClient? llm = llmOptions.IsConfigured ? new CyteLlmClient(llmOptions) : null;

if (llm == null)
{
    Console.WriteLine(" ! CyteLlm:ClientId / CyteLlm:ClientSecret not set - AI analysis and chat are unavailable.");
}
```

Add `using HazardRecon.Core.Llm;` to the top of the file.

- [ ] **Step 6: Add `GET /api/models`**

Add this endpoint immediately before the existing `// GET /health` comment:

```csharp
// GET /api/models
app.MapGet("/api/models", async () =>
{
    if (llm == null)
    {
        return Results.Json(new { error = "The LLM gateway is not configured (CyteLlm:ClientId / ClientSecret missing)." }, statusCode: 503);
    }

    try
    {
        IReadOnlyList<LlmModel> models = await llm.ListModelsAsync();
        return Results.Ok(models.Select(m => new
        {
            id = m.Id,
            provider = m.Provider,
            friendlyName = m.FriendlyName,
            modelName = m.ModelName
        }));
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Could not list models - {ex.Message}" }, statusCode: 503);
    }
});
```

- [ ] **Step 7: Accept `model_id` on run and use it**

In the `POST /api/run` handler, after the existing `rid` is read and the job is found, add:

```csharp
    string? modelId = doc.RootElement.TryGetProperty("model_id", out var modelProp) ? modelProp.GetString() : null;
    job.ModelId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();
```

Then replace the temporary `engine.Run(...)` line from Task 4 Step 7 with:

```csharp
            AiAnalysisService? analyst = (llm != null && capturedJob.ModelId != null)
                ? new AiAnalysisService(llm, capturedJob.ModelId)
                : null;

            ReconciliationRunResult outResult = engine.Run(
                capturedJob.Roots, capturedJob.Outdir, logger: Logger,
                analyze: analyst != null, analyst: analyst);

            capturedJob.AnalysisPayload = AiAnalysisService.BuildAnalysisPayload(outResult.Results);
```

Place the `AnalysisPayload` assignment immediately after the `engine.Run` call, before `setSummaries` is built.

- [ ] **Step 8: Give the chat the real client, model and figures**

Replace the temporary chat lines from Task 5 Step 4 with:

```csharp
    ChatService chatService = new(llm, job.ModelId);
    var chatRes = chatService.ProcessQuestion(message, job.AnalysisPayload ?? new Dictionary<string, object>());
```

- [ ] **Step 9: Build and check the endpoint by hand**

```bash
cd /c/Code/Cyte/hazard-rate-recon-dotnet
dotnet build src/HazardRecon.Web/HazardRecon.Web.csproj
```

Expected: `Build succeeded. 0 Error(s)`.

Then, in one shell:

```bash
cd /c/Code/Cyte/hazard-rate-recon-dotnet/src/HazardRecon.Web
dotnet run --no-build
```

and in another:

```bash
curl -s http://127.0.0.1:5000/api/models
```

Expected: a JSON array with two entries whose `friendlyName` values are `Google Gemini 2.5 Pro` and `Azure OpenAI GPT-4o`. Stop the server afterwards.

- [ ] **Step 10: Run the whole suite**

Run: `dotnet test`
Expected: PASS — 56 passed.

- [ ] **Step 11: Commit**

```bash
git add src/HazardRecon.Web
git commit -m "feat: expose model list and run the selected model from the web app"
```

---

## Task 8: CLI `--model` flag

**Files:**
- Modify: `src/HazardRecon.Cli/HazardRecon.Cli.csproj` (add `UserSecretsId` and configuration packages)
- Modify: `src/HazardRecon.Cli/Program.cs` (whole argument loop and run call)

**Interfaces:**
- Consumes: `CyteLlmOptions`, `CyteLlmClient`, `ModelResolver`, `LlmModel` (Tasks 1, 6); `AiAnalysisService` and `Run(..., analyst:)` (Task 4).
- Produces: nothing other tasks depend on.

- [ ] **Step 1: Add configuration support to the CLI project**

In `src/HazardRecon.Cli/HazardRecon.Cli.csproj`, add to the `<PropertyGroup>`:

```xml
    <UserSecretsId>hazard-recon-cyte-llm</UserSecretsId>
```

and add a new item group:

```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.UserSecrets" Version="10.0.0" />
  </ItemGroup>
```

The same `UserSecretsId` as the Web project is intentional: both read one secret store.

- [ ] **Step 2: Add an appsettings file for the CLI**

Create `src/HazardRecon.Cli/appsettings.json`:

```json
{
  "CyteLlm": {
    "TokenUrl": "https://auth-qa.cyte.co.za/oauth/token",
    "Audience": "https://coreapi-qa.cyte.co.za/api/",
    "ApiBaseUrl": "https://coreapi-qa.cyte.co.za/api"
  }
}
```

And make it copy to the output directory by adding to the CLI `.csproj`:

```xml
  <ItemGroup>
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Replace `src/HazardRecon.Cli/Program.cs`**

```csharp
using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using Microsoft.Extensions.Configuration;

namespace HazardRecon.Cli;

class Program
{
    static int Main(string[] args)
    {
        var roots = new List<string>();
        string outdir = "output";
        bool noAnalysis = false;
        string? modelFragment = null;

        for (int i = 0; i < args.Length; i++)
        {
            // --root can be supplied multiple times, OR as a single space-separated value
            if ((args[i] == "--root" || args[i] == "--roots") && i + 1 < args.Length)
            {
                roots.Add(args[i + 1]);
                i++;
            }
            else if (args[i] == "--outdir" && i + 1 < args.Length)
            {
                outdir = args[i + 1];
                i++;
            }
            else if (args[i] == "--model" && i + 1 < args.Length)
            {
                modelFragment = args[i + 1];
                i++;
            }
            else if (args[i] == "--no-analysis")
            {
                noAnalysis = true;
            }
            else if (!args[i].StartsWith('-'))
            {
                // bare positional argument — treat as a root folder
                roots.Add(args[i]);
            }
        }

        if (roots.Count == 0)
        {
            Console.WriteLine("Usage: hazard-recon --root <folder> [--root <folder2> ...] [--outdir <output>] [--model <name>] [--no-analysis]");
            Console.WriteLine("       Tip: --root may be repeated up to 4 times for multi-period runs.");
            Console.WriteLine("       --model takes a model id or part of its name; omit it to use the first available model.");
            Console.WriteLine("Error: at least one --root argument is required.");
            return 1;
        }

        Directory.CreateDirectory(outdir);

        AiAnalysisService? analyst = null;
        if (!noAnalysis)
        {
            IConfigurationRoot config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddUserSecrets<Program>(optional: true)
                .AddEnvironmentVariables()
                .Build();

            CyteLlmOptions llmOptions = new();
            config.GetSection("CyteLlm").Bind(llmOptions);

            if (!llmOptions.IsConfigured)
            {
                Console.WriteLine("! CyteLlm:ClientId / CyteLlm:ClientSecret not set - continuing without AI analysis.");
            }
            else
            {
                try
                {
                    CyteLlmClient client = new(llmOptions);
                    IReadOnlyList<LlmModel> models = client.ListModelsAsync().GetAwaiter().GetResult();
                    LlmModel? chosen = ModelResolver.Resolve(models, modelFragment);

                    if (chosen == null)
                    {
                        Console.WriteLine($"Error: no model matches '{modelFragment}'. Available models:");
                        foreach (LlmModel m in models)
                        {
                            Console.WriteLine($"  {m.Id}  {m.FriendlyName}  ({m.ModelName})");
                        }
                        return 1;
                    }

                    Console.WriteLine($"Using model: {chosen.FriendlyName} ({chosen.ModelName})");
                    analyst = new AiAnalysisService(client, chosen.Id);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"! Could not reach the LLM gateway ({ex.Message}) - continuing without AI analysis.");
                }
            }
        }

        try
        {
            var engine = new ReconciliationEngine();
            ReconciliationRunResult result = engine.Run(roots, outdir, analyze: analyst != null, analyst: analyst);

            Console.WriteLine($"\nWorkbook : {Path.GetFullPath(Path.Combine(outdir, result.Workbook))}");
            Console.WriteLine($"Dashboard: {Path.GetFullPath(Path.Combine(outdir, result.Dashboard))}");
            if (!string.IsNullOrEmpty(result.Memo))
                Console.WriteLine($"Memo     : {Path.GetFullPath(Path.Combine(outdir, result.Memo))}");

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
```

An unmatched `--model` is a hard error (exit 1) because the user asked for something specific. An unreachable gateway is a warning, because analysis is optional.

- [ ] **Step 4: Verify the usage text and an unmatched model**

```bash
cd /c/Code/Cyte/hazard-rate-recon-dotnet
dotnet build src/HazardRecon.Cli/HazardRecon.Cli.csproj
dotnet run --project src/HazardRecon.Cli --no-build
```

Expected: usage text mentioning `--model`, exit code 1.

```bash
dotnet run --project src/HazardRecon.Cli --no-build -- --root "C:/temp/hr_repro/data" --outdir "C:/temp/hr_cli_out" --model llama
```

Expected: `Error: no model matches 'llama'.` followed by the two available models, exit code 1.

- [ ] **Step 5: Verify a real CLI run with a model**

```bash
dotnet run --project src/HazardRecon.Cli --no-build -- --root "C:/temp/hr_repro/data" --outdir "C:/temp/hr_cli_out" --model gemini
```

Expected: `Using model: Google Gemini 2.5 Pro (gemini-2.5-pro)`, then the workbook / dashboard / memo paths. Confirm `C:/temp/hr_cli_out/analysis_memo.docx` exists.

- [ ] **Step 6: Run the whole suite**

Run: `dotnet test`
Expected: PASS — 56 passed.

- [ ] **Step 7: Commit**

```bash
git add src/HazardRecon.Cli
git commit -m "feat: let the CLI choose a model with --model"
```

---

## Task 9: Model picker in the browser

**Files:**
- Modify: `src/HazardRecon.Web/wwwroot/index.html` (add the select to the run card)
- Modify: `src/HazardRecon.Web/wwwroot/app.js` (load, persist, and send the selection)
- Modify: `src/HazardRecon.Web/wwwroot/app.css` (style the row)
- Test: `tests/client/app.harness.mjs` (add a scenario)

**Interfaces:**
- Consumes: `GET /api/models` and `POST /api/run` with `model_id` (Task 7).
- Produces: nothing other tasks depend on.

- [ ] **Step 1: Add the control to the run card**

In `src/HazardRecon.Web/wwwroot/index.html`, immediately before the `<button ... id="btn-run">` element, insert:

```html
      <div class="modelrow">
        <label for="model">AI analysis model</label>
        <select id="model"></select>
        <span class="hint" id="model-note"></span>
      </div>
```

The `<select>` is intentionally empty. Every option, including "Skip AI analysis", is
created by `loadModels()` in Step 5, so the option list has one source of truth and
the harness can assert on it.

- [ ] **Step 2: Style it**

Append to `src/HazardRecon.Web/wwwroot/app.css`:

```css
.modelrow { display:flex; align-items:center; gap:10px; margin:10px 0; flex-wrap:wrap; }
.modelrow label { font-weight:600; font-size:13px; }
.modelrow select { padding:6px 8px; border:1px solid #cfd8e3; border-radius:6px; font-size:13px; background:#fff; }
.modelrow select:disabled { background:#f0f2f5; color:#5b6b7f; }
.modelrow .hint { margin:0; }
```

- [ ] **Step 3: Write the failing harness scenario**

In `tests/client/app.harness.mjs`, add this scenario function immediately before the final `for (const s of [...])` line:

```javascript
/* ---------------- F: model selection ---------------- */
async function scenarioF() {
  console.log("F) model picker populates and is sent with the run");
  let runBody = null;
  const models = [
    { id: "72e110c8", provider: 1, friendlyName: "Google Gemini 2.5 Pro", modelName: "gemini-2.5-pro" },
    { id: "5f3283d8", provider: 0, friendlyName: "Azure OpenAI GPT-4o", modelName: "gpt4o" },
  ];
  const h = boot((url, opts) => {
    if (url === "/api/models") return Promise.resolve(jsonRes(200, models));
    if (url === "/api/run") { runBody = JSON.parse(opts.body); return Promise.resolve(jsonRes(200, { run_id: "RID1", status: "running" })); }
    if (url.startsWith("/api/job/")) return Promise.resolve(jsonRes(200, { status: "running", log: [] }));
    return Promise.resolve(jsonRes(200, {}));
  });

  await tick(); await tick(); await tick();
  const sel = h.$get("#model");
  check("skip option is present and first", sel.children[0]?.value === "");
  check("both models added as options", sel.children.length === 3, `got ${sel.children.length}`);

  sel.value = "5f3283d8";
  h.ctx.beginRun();
  await tick(); await tick();
  check("model_id sent with the run", runBody?.model_id === "5f3283d8", `sent ${JSON.stringify(runBody)}`);
}

/* ---------------- G: model list unavailable ---------------- */
async function scenarioG() {
  console.log("G) /api/models fails");
  let runBody = null;
  const h = boot((url, opts) => {
    if (url === "/api/models") return Promise.resolve(jsonRes(503, { error: "gateway not configured" }));
    if (url === "/api/run") { runBody = JSON.parse(opts.body); return Promise.resolve(jsonRes(200, { run_id: "RID1", status: "running" })); }
    if (url.startsWith("/api/job/")) return Promise.resolve(jsonRes(200, { status: "running", log: [] }));
    return Promise.resolve(jsonRes(200, {}));
  });

  await tick(); await tick(); await tick();
  check("select disabled", h.$get("#model").disabled === true);
  check("reason shown", /not configured/.test(h.$get("#model-note").textContent || ""),
    `note='${h.$get("#model-note").textContent}'`);

  h.ctx.beginRun();
  await tick(); await tick();
  check("run still starts, without a model", runBody !== null && !runBody.model_id);
}
```

Then change the final loop line to include the new scenarios:

```javascript
for (const s of [scenarioA, scenarioB, scenarioC, scenarioD, scenarioE, scenarioF, scenarioG]) { await s(); console.log("") }
```

No harness plumbing changes are needed:

- `boot` assigns the responder straight to `ctx.fetch`, and `app.js` already calls
  `fetch(url, { method, headers, body })` for POSTs, so a scenario can declare
  `(url, opts)` and read `opts.body`. Existing scenarios that declare only `(url)`
  are unaffected — extra JS arguments are ignored.
- `makeEl` already exposes a `children` getter that `appendChild` pushes onto, so
  `sel.children` works as-is.
- `opts` is `undefined` for GET requests, so only read `opts.body` on the
  `/api/run` branch, as the scenarios above do.

- [ ] **Step 4: Run the harness to verify the new scenarios fail**

Run: `node tests/client/app.harness.mjs`
Expected: FAIL — scenario F reports `both models added as options -> got 1`, scenario G reports `select disabled` failing.

- [ ] **Step 5: Load, persist and send the model**

In `src/HazardRecon.Web/wwwroot/app.js`, add after the `restorePaths` IIFE:

```javascript
/* ---------- step 2: model ---------- */
function addModelOption(value, label) {
  const o = document.createElement("option");
  o.value = value;
  o.textContent = label;
  $("#model").appendChild(o);
  return o;
}

function loadModels() {
  const sel = $("#model");
  const note = $("#model-note");
  addModelOption("", "Skip AI analysis");
  return fetch("/api/models")
    .then(readJson)
    .then(({ ok, j }) => {
      if (!ok || !Array.isArray(j)) {
        sel.disabled = true;
        note.textContent = (j && j.error) || "Model list unavailable - runs will skip AI analysis.";
        return;
      }
      j.forEach(m => addModelOption(m.id, m.friendlyName));
      const saved = localStorage.getItem("hr_model") || "";
      sel.value = j.some(m => m.id === saved) ? saved : "";
      note.textContent = "Analysis adds roughly 25 seconds to a run.";
    })
    .catch(e => {
      sel.disabled = true;
      note.textContent = "Model list unavailable - " + e.message;
    });
}

$("#model").addEventListener("change", () => localStorage.setItem("hr_model", $("#model").value));
loadModels();
```

Then in `startRun`, change the request body to carry the selection:

```javascript
    body: JSON.stringify({ run_id: RUN_ID, model_id: $("#model").value || null })
```

- [ ] **Step 6: Run the harness to verify it passes**

Run: `node tests/client/app.harness.mjs`
Expected: `ALL SCENARIOS PASSED`.

- [ ] **Step 7: Verify in a real browser**

Rebuild and start the server:

```bash
cd /c/Code/Cyte/hazard-rate-recon-dotnet
dotnet build src/HazardRecon.Web/HazardRecon.Web.csproj
cd src/HazardRecon.Web && dotnet run --no-build
```

Open http://127.0.0.1:5000, confirm the dropdown lists **Skip AI analysis**, **Google Gemini 2.5 Pro**, **Azure OpenAI GPT-4o**. Run the folder
`C:\Users\CharlesLehong.AzureAD\Downloads\4. DEBUG FILE 30 JUNE 2025 3 MONTHS\3. DEBUG FILE 30 JUNE 2026 3 MONTHS`
with Gemini selected. Expected: the run log shows `Generating AI analysis` then `AI analysis generated (N output tokens)`; the results page shows the AI panel and an `analysis_memo.docx` download. Then ask a question in "Ask about this run" and confirm a real answer comes back. Stop the server.

- [ ] **Step 8: Commit**

```bash
git add src/HazardRecon.Web/wwwroot tests/client/app.harness.mjs
git commit -m "feat: let users pick the analysis model in the browser"
```

---

## Task 10: Live smoke script

A manual script that exercises the real QA gateway, kept out of the unit suite so tests stay offline.

**Files:**
- Create: `tests/client/cyte-smoke.mjs`

**Interfaces:**
- Consumes: the running web app's `GET /api/models`.
- Produces: nothing.

- [ ] **Step 1: Write the script**

Create `tests/client/cyte-smoke.mjs`:

```javascript
/* Live check against the running web app's model endpoint, and through it the
   real Cyte gateway. Start the server first, then:

     node tests/client/cyte-smoke.mjs [baseUrl]

   Exits non-zero if the gateway is not reachable or returns no models. */
const base = process.argv[2] || "http://127.0.0.1:5000";

let failures = 0;
const check = (name, cond, detail) => {
  if (cond) console.log(`  PASS  ${name}`);
  else { console.log(`  FAIL  ${name}${detail ? " -> " + detail : ""}`); failures++; }
};

// An unreachable server is the most likely failure for a diagnostic script, so
// it must report the URL and the reason rather than an opaque fetch stack trace.
let res, text;
try {
  res = await fetch(`${base}/api/models`);
  text = await res.text();
} catch (e) {
  console.error(`ERROR: could not reach ${base}/api/models`);
  console.error(`  ${e.message}`);
  console.error(`  Is the web app running? Start it with: cd src/HazardRecon.Web && dotnet run`);
  process.exit(1);
}

let body = null;
try { body = JSON.parse(text); } catch (_) { }

console.log(`GET ${base}/api/models -> ${res.status}`);
check("responded 200", res.status === 200, text.slice(0, 200));
check("returned an array", Array.isArray(body), typeof body);

if (Array.isArray(body)) {
  check("at least one model", body.length > 0, `got ${body.length}`);
  for (const m of body) {
    check(`model has id + friendlyName (${m.friendlyName || "?"})`,
      Boolean(m.id) && Boolean(m.friendlyName), JSON.stringify(m));
  }
  console.log("\nmodels:");
  for (const m of body) console.log(`  ${m.id}  provider=${m.provider}  ${m.friendlyName}  (${m.modelName})`);
}

console.log(failures === 0 ? "\nSMOKE PASSED" : `\n${failures} CHECK(S) FAILED`);
process.exit(failures === 0 ? 0 : 1);
```

- [ ] **Step 2: Run it against a live server**

```bash
cd /c/Code/Cyte/hazard-rate-recon-dotnet/src/HazardRecon.Web && dotnet run --no-build &
sleep 8
cd /c/Code/Cyte/hazard-rate-recon-dotnet && node tests/client/cyte-smoke.mjs
```

Expected: `SMOKE PASSED`, listing both models. Stop the server afterwards.

- [ ] **Step 3: Confirm nothing reads `ANTHROPIC_API_KEY` any more**

```bash
cd /c/Code/Cyte/hazard-rate-recon-dotnet
grep -rn "ANTHROPIC_API_KEY" src tests || echo "clean"
```

Expected: `clean`.

- [ ] **Step 4: Final full verification**

```bash
cd /c/Code/Cyte/hazard-rate-recon-dotnet
dotnet test
node tests/client/app.harness.mjs

# resolve the most recent run that produced a dashboard, then check its heat map
RUNS=src/HazardRecon.Web/bin/Debug/net10.0/runs
RID=$(ls -1 "$RUNS" | while read d; do [ -f "$RUNS/$d/output/reconciliation_dashboard.html" ] && echo "$d"; done | tail -1)
node tests/client/dashboard-heat.mjs "$RUNS/$RID/output/reconciliation_dashboard.html"
```

Expected: 44 passed; `ALL SCENARIOS PASSED`; `ALL HEAT-MAP CHECKS PASSED`.

- [ ] **Step 5: Commit**

```bash
git add tests/client/cyte-smoke.mjs
git commit -m "test: add live smoke check for the Cyte gateway"
```

---

## Appendix: expected test counts

| After task | Total tests |
|---|---|
| Baseline | 20 |
| 1 | 26 |
| 2 | 32 |
| 3 | 38 |
| 4 | 43 |
| 5 | 49 |
| 6 | 56 |
| 7–10 | 56 |

Tasks 7–10 add no xUnit tests; they are verified by the harness, the smoke script and manual browser checks.
