using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>Which model answers a question about a run - see ChatModel.</summary>
public class ChatModelTests
{
    [Fact]
    public void TestTheRunsOwnModelAnswers()
    {
        // it wrote the memo sitting beside the conversation; the two must agree
        Assert.Equal("gemini-2.5-pro", ChatModel.Choose("gemini-2.5-pro", null));
    }

    [Fact]
    public void TestTheRunsModelWinsOverOneAskedFor()
    {
        Assert.Equal("gemini-2.5-pro", ChatModel.Choose("gemini-2.5-pro", "gpt-4o"));
    }

    [Fact]
    public void TestAskedModelAnswersWhenTheRunHasNone()
    {
        // the case this exists for: a run reconciled with AI analysis skipped
        Assert.Equal("gpt-4o", ChatModel.Choose(null, "gpt-4o"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TestABlankRunModelFallsThroughToTheAskedOne(string runModel)
    {
        Assert.Equal("gpt-4o", ChatModel.Choose(runModel, "gpt-4o"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData("", "   ")]
    public void TestNothingToAnswerWithIsNull(string? runModel, string? askedModel)
    {
        // null rather than "", so ChatService reports it instead of calling the
        // gateway with an empty model id
        Assert.Null(ChatModel.Choose(runModel, askedModel));
    }

    [Fact]
    public void TestSurroundingWhitespaceIsTrimmed()
    {
        Assert.Equal("gpt-4o", ChatModel.Choose(null, "  gpt-4o  "));
        Assert.Equal("gemini-2.5-pro", ChatModel.Choose("  gemini-2.5-pro  ", null));
    }
}
