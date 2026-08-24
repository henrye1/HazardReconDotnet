using System.Text.Json;
using HazardRecon.Web.Uploads;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>
/// The confirmation body's parser. Before it took arrays, a multi-column field
/// threw InvalidOperationException out of GetString() - a 500 for what is a
/// perfectly well-formed request - so each shape is pinned here.
/// </summary>
public class MappingRequestTests
{
    private static JsonElement Set(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void TestASingleStringBecomesAOneColumnList()
    {
        var mapping = MappingRequest.ReadMapping(
            Set("""{"exposure":{"LoanAccountNumber":"Acct"}}"""), "exposure");

        Assert.Equal(new[] { "Acct" }, mapping["LoanAccountNumber"]);
    }

    [Fact]
    public void TestAnArrayKeepsThePickedOrder()
    {
        var mapping = MappingRequest.ReadMapping(
            Set("""{"exposure":{"AgingBuckets":["90 Days","60 Days"]}}"""), "exposure");

        Assert.Equal(new[] { "90 Days", "60 Days" }, mapping["AgingBuckets"]);
    }

    [Fact]
    public void TestBothShapesCanAppearInOneFile()
    {
        var mapping = MappingRequest.ReadMapping(
            Set("""
            {"exposure":{"LoanAccountNumber":"Acct","TransactionNumber":"Txn","AgingBuckets":["60 Days"]}}
            """), "exposure");

        Assert.Equal(new[] { "Acct" }, mapping["LoanAccountNumber"]);
        Assert.Equal(new[] { "Txn" }, mapping["TransactionNumber"]);
        Assert.Equal(new[] { "60 Days" }, mapping["AgingBuckets"]);
    }

    /// <summary>
    /// An empty array is not the same as an absent field: it is the user saying "no
    /// buckets", which the loader refuses. Dropping it here would turn that refusal
    /// into a literal read of a column named after the field.
    /// </summary>
    [Fact]
    public void TestAnEmptyArrayIsRecordedAsAnEmptySelection()
    {
        var mapping = MappingRequest.ReadMapping(
            Set("""{"exposure":{"AgingBuckets":[]}}"""), "exposure");

        Assert.True(mapping.ContainsKey("AgingBuckets"));
        Assert.Empty(mapping["AgingBuckets"]);
    }

    [Fact]
    public void TestDuplicatesAreDroppedSoAColumnIsNotSummedTwice()
    {
        var mapping = MappingRequest.ReadMapping(
            Set("""{"exposure":{"AgingBuckets":["60 Days","60 Days","90 Days"]}}"""), "exposure");

        Assert.Equal(new[] { "60 Days", "90 Days" }, mapping["AgingBuckets"]);
    }

    [Fact]
    public void TestBlankAndNonStringEntriesAreSkipped()
    {
        var mapping = MappingRequest.ReadMapping(
            Set("""{"exposure":{"AgingBuckets":["60 Days","",null,7,"90 Days"]}}"""), "exposure");

        Assert.Equal(new[] { "60 Days", "90 Days" }, mapping["AgingBuckets"]);
    }

    [Fact]
    public void TestAFieldOfSomeOtherShapeIsIgnoredRatherThanFailing()
    {
        var mapping = MappingRequest.ReadMapping(
            Set("""{"exposure":{"LoanAccountNumber":{"nested":"object"},"TransactionNumber":"Txn"}}"""), "exposure");

        Assert.False(mapping.ContainsKey("LoanAccountNumber"));
        Assert.Equal(new[] { "Txn" }, mapping["TransactionNumber"]);
    }

    [Fact]
    public void TestAnAbsentFileKindIsEmpty()
    {
        var mapping = MappingRequest.ReadMapping(Set("""{"exposure":{"A":"B"}}"""), "writeoff");

        Assert.Empty(mapping);
    }

    [Fact]
    public void TestAnEmptyStringIsAnEmptySelectionNotAColumn()
    {
        var mapping = MappingRequest.ReadMapping(
            Set("""{"exposure":{"AmountOutstanding":""}}"""), "exposure");

        Assert.True(mapping.ContainsKey("AmountOutstanding"));
        Assert.Empty(mapping["AmountOutstanding"]);
    }

    [Theory]
    [InlineData("""{"exposure_has_headers":true}""", true)]
    [InlineData("""{"exposure_has_headers":false}""", false)]
    [InlineData("""{"exposure_has_headers":"yes"}""", null)]
    [InlineData("""{}""", null)]
    public void TestTheHeaderFlagIsReadOnlyWhenItIsActuallyABoolean(string json, bool? expected)
    {
        Assert.Equal(expected, MappingRequest.ReadHasHeaders(Set(json), "exposure"));
    }
}
