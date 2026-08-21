using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

public class RunTypeLookupTests
{
    [Theory]
    [InlineData(RunTypeLookup.Lending, (short)1)]
    [InlineData(RunTypeLookup.TradeReceivables, (short)2)]
    public void TestEachCodeRoundTripsThroughItsId(string code, short id)
    {
        Assert.Equal(id, RunTypeLookup.IdOf(code));
        Assert.Equal(code, RunTypeLookup.CodeOf(id));
    }

    [Fact]
    public void TestTheDefaultIsLendingAndMatchesTheColumnDefault()
    {
        Assert.Equal(RunTypeLookup.Lending, RunTypeLookup.Default);
        Assert.Equal((short)1, RunTypeLookup.IdOf(RunTypeLookup.Default));
    }

    [Theory]
    [InlineData("mortgages")]
    [InlineData("")]
    [InlineData(null)]
    public void TestAnUnknownCodeIsNotKnown(string? code) =>
        Assert.False(RunTypeLookup.IsKnown(code));

    [Fact]
    public void TestAKnownCodeIsKnown() =>
        Assert.True(RunTypeLookup.IsKnown(RunTypeLookup.TradeReceivables));

    /// <summary>
    /// IsKnown exists so the endpoint never reaches this - a run type comes off
    /// the wire, and a throw here would be a 500 for what is a bad request.
    /// </summary>
    [Fact]
    public void TestAnUnknownCodeThrowsRatherThanGuessing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RunTypeLookup.IdOf("mortgages"));
        Assert.Throws<ArgumentOutOfRangeException>(() => RunTypeLookup.CodeOf(99));
    }
}
