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
