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
