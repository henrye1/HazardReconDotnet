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
        Assert.Equal("Chat is unavailable - the LLM gateway is not configured.", res.ErrorMessage);
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
