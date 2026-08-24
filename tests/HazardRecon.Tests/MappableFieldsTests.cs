using HazardRecon.Core.Models;
using Xunit;

namespace HazardRecon.Tests;

public class MappableFieldsTests
{
    [Fact]
    public void TestWriteoffListsAllFourFields()
    {
        Assert.Equal(
            new[] { "LoanAccountNumber", "CustomerId", "Amount", "ReportDate" },
            MappableFields.Writeoff.Select(f => f.Field));
    }

    [Fact]
    public void TestExposureListsBothFields()
    {
        Assert.Equal(
            new[] { "LoanAccountNumber", "AmountOutstanding" },
            MappableFields.Exposure.Select(f => f.Field));
    }

    /// <summary>
    /// An age analysis has no loan account number at all - the customer number is
    /// the identifier, and the aging columns are the amount.
    /// </summary>
    [Fact]
    public void TestAgeAnalysisListsItsTwoFields()
    {
        Assert.Equal(
            new[] { "ClientNumber", "AgingBuckets" },
            MappableFields.AgeAnalysis.Select(f => f.Field));
    }

    /// <summary>
    /// The write-off file's two identifiers swap roles by run type: whichever the
    /// defaults are keyed on is the join key, and the other is carried.
    /// </summary>
    [Fact]
    public void TestTheWriteoffKeyFieldFollowsTheRunType()
    {
        Assert.Equal("LoanAccountNumber", MappableFields.WriteoffFor(EngineRunType.Lending)[0].Field);
        Assert.Equal("CustomerId", MappableFields.WriteoffFor(EngineRunType.TradeReceivables)[0].Field);

        // and the notes swap with them, or one of them is telling the reader the
        // opposite of what the loader does
        Assert.Contains("join key", MappableFields.WriteoffFor(EngineRunType.TradeReceivables)[0].Note);
        Assert.Contains("not used for matching", MappableFields.WriteoffFor(EngineRunType.TradeReceivables)[1].Note);
    }

    /// <summary>
    /// Only the aging buckets take several columns. Marking anything else Multiple
    /// would send an array where the loaders read one column.
    /// </summary>
    [Fact]
    public void TestOnlyTheAgingBucketFieldTakesSeveralColumns()
    {
        Assert.Equal(
            new[] { "AgingBuckets" },
            MappableFields.Writeoff
                .Concat(MappableFields.WriteoffByCustomer)
                .Concat(MappableFields.Exposure)
                .Concat(MappableFields.AgeAnalysis)
                .Where(f => f.Multiple)
                .Select(f => f.Field));
    }

    [Fact]
    public void TestTheRunTypeChoosesTheExposureFieldList()
    {
        Assert.Same(MappableFields.Exposure, MappableFields.ExposureFor(EngineRunType.Lending));
        Assert.Same(MappableFields.AgeAnalysis, MappableFields.ExposureFor(EngineRunType.TradeReceivables));
    }

    [Fact]
    public void TestEveryFieldHasANonEmptyNote()
    {
        Assert.All(
            MappableFields.Writeoff
                .Concat(MappableFields.WriteoffByCustomer)
                .Concat(MappableFields.Exposure)
                .Concat(MappableFields.AgeAnalysis),
            f => Assert.False(string.IsNullOrWhiteSpace(f.Note)));
    }
}
