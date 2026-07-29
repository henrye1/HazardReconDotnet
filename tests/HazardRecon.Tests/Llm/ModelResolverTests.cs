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
