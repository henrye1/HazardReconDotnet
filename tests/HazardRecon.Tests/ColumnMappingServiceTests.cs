using HazardRecon.Core.Llm;
using HazardRecon.Core.Models;
using HazardRecon.Core.Services;
using HazardRecon.Tests.Llm;
using Xunit;

namespace HazardRecon.Tests;

public class ColumnMappingServiceTests
{
    private static readonly IReadOnlyList<MappingFieldSpec> Fields = MappableFields.Exposure;

    [Fact]
    public void TestAnExactHeaderMatchNeedsNoAiCall()
    {
        FakeLlmClient client = new();
        ColumnMappingService service = new(client, "model-1");

        var resolved = service.Resolve(
            headers: new[] { "LoanAccountNumber", "AmountOutstanding" },
            sampleRows: new List<IReadOnlyList<string>>(),
            fields: Fields,
            savedMapping: null);

        Assert.All(resolved, r => Assert.Equal("header_match", r.Source));
        Assert.Equal(0, client.ChatCalls);
    }

    [Fact]
    public void TestASavedMappingIsUsedBeforeAskingTheAi()
    {
        FakeLlmClient client = new();
        ColumnMappingService service = new(client, "model-1");
        var saved = new Dictionary<string, IReadOnlyList<string>>
        {
            ["LoanAccountNumber"] = new[] { "Column 1" },
            ["AmountOutstanding"] = new[] { "Column 3" }
        };

        var resolved = service.Resolve(
            headers: null,
            sampleRows: new List<IReadOnlyList<string>> { new[] { "A1", "100" } },
            fields: Fields,
            savedMapping: saved);

        Assert.All(resolved, r => Assert.Equal("saved", r.Source));
        Assert.Equal(0, client.ChatCalls);
    }

    /// <summary>
    /// Which aging buckets count as defaulted is a business rule, not a column
    /// name the model can recognise - so it is never asked, and a wrong-but-
    /// confident answer can never reach the user.
    /// </summary>
    [Fact]
    public void TestAMultiValuedFieldIsNeverSentToTheAi()
    {
        FakeLlmClient client = new() { ReplyContent = """{"AgingBuckets":{"column":"Current","confidence":0.95}}""" };
        ColumnMappingService service = new(client, "model-1");

        var resolved = service.Resolve(
            headers: new[] { "Acct", "Txn", "Current", "90 Days" },
            sampleRows: new List<IReadOnlyList<string>> { new[] { "A1", "T1", "10", "20" } },
            fields: MappableFields.AgeAnalysis,
            savedMapping: null);

        ResolvedField buckets = resolved.Single(r => r.Field == "AgingBuckets");
        Assert.Equal("unmapped", buckets.Source);
        Assert.Empty(buckets.Columns);

        // the account and transaction columns are still guessed as usual
        Assert.Equal(1, client.ChatCalls);
    }

    [Fact]
    public void TestASavedBucketSelectionComesBackInOrder()
    {
        ColumnMappingService service = new(null, null);
        var saved = new Dictionary<string, IReadOnlyList<string>>
        {
            ["AgingBuckets"] = new[] { "60 Days", "90 Days" }
        };

        var resolved = service.Resolve(
            headers: new[] { "Acct", "Txn", "Current", "60 Days", "90 Days" },
            sampleRows: new List<IReadOnlyList<string>> { new[] { "A1", "T1", "1", "2", "3" } },
            fields: MappableFields.AgeAnalysis,
            savedMapping: saved);

        ResolvedField buckets = resolved.Single(r => r.Field == "AgingBuckets");
        Assert.Equal("saved", buckets.Source);
        Assert.Equal(new[] { "60 Days", "90 Days" }, buckets.Columns);
    }

    [Fact]
    public void TestAnUnmatchedFieldFallsBackToAnAiGuess()
    {
        FakeLlmClient client = new()
        {
            ReplyContent = """{"LoanAccountNumber": {"column": "Column 1", "confidence": 0.97}, "AmountOutstanding": {"column": "Column 3", "confidence": 0.88}}"""
        };
        ColumnMappingService service = new(client, "model-1");

        var resolved = service.Resolve(
            headers: null,
            sampleRows: new List<IReadOnlyList<string>> { new[] { "A1", "2026-06-30", "100", "Stage 2" } },
            fields: Fields,
            savedMapping: null);

        var byField = resolved.ToDictionary(r => r.Field);
        Assert.Equal("Column 1", byField["LoanAccountNumber"].Column);
        Assert.Equal(0.97, byField["LoanAccountNumber"].Confidence);
        Assert.Equal("ai_guess", byField["LoanAccountNumber"].Source);
        Assert.Equal(1, client.ChatCalls);
    }

    [Fact]
    public void TestAFieldTheAiCannotGuessComesBackUnmapped()
    {
        FakeLlmClient client = new() { ReplyContent = """{"LoanAccountNumber": {"column": "Column 1", "confidence": 0.9}}""" };
        ColumnMappingService service = new(client, "model-1");

        var resolved = service.Resolve(null, new List<IReadOnlyList<string>>(), Fields, null);

        var amountOutstanding = resolved.Single(r => r.Field == "AmountOutstanding");
        Assert.Equal("unmapped", amountOutstanding.Source);
        Assert.Null(amountOutstanding.Column);
    }

    [Fact]
    public void TestAThrownExceptionDegradesToUnmappedRatherThanThrowing()
    {
        FakeLlmClient client = new() { ThrowOnChat = new LlmException("gateway down") };
        ColumnMappingService service = new(client, "model-1");

        var resolved = service.Resolve(null, new List<IReadOnlyList<string>>(), Fields, null);

        Assert.All(resolved, r => Assert.Equal("unmapped", r.Source));
    }

    [Fact]
    public void TestUnparseableJsonDegradesToUnmappedRatherThanThrowing()
    {
        FakeLlmClient client = new() { ReplyContent = "not json at all" };
        ColumnMappingService service = new(client, "model-1");

        var resolved = service.Resolve(null, new List<IReadOnlyList<string>>(), Fields, null);

        Assert.All(resolved, r => Assert.Equal("unmapped", r.Source));
    }

    [Fact]
    public void TestANullClientStillResolvesHeaderMatchesAndSavedMappings()
    {
        // the LLM gateway being unconfigured must not lose the free resolution
        // steps that need no AI call at all
        ColumnMappingService service = new(client: null, modelId: null);

        var resolved = service.Resolve(
            headers: new[] { "LoanAccountNumber" },
            sampleRows: new List<IReadOnlyList<string>>(),
            fields: new[] { MappableFields.Exposure[0] },
            savedMapping: null);

        Assert.Equal("header_match", resolved[0].Source);
    }

    [Fact]
    public void TestANullClientFallsBackToUnmappedWhenNoHeaderOrSavedMatch()
    {
        ColumnMappingService service = new(client: null, modelId: null);

        var resolved = service.Resolve(null, new List<IReadOnlyList<string>>(), Fields, null);

        Assert.All(resolved, r => Assert.Equal("unmapped", r.Source));
    }
}
