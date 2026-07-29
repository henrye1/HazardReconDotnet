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
                ? (HttpStatusCode.OK, TokenJson(86400))
                : (HttpStatusCode.OK, ModelsJson));

        CyteLlmClient client = new(Options(), handler, () => now);

        await client.ListModelsAsync();
        now = now.AddSeconds(86400 + 1);    // past the 24h lifetime (and its margin)
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
