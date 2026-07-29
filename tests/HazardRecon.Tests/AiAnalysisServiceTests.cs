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
