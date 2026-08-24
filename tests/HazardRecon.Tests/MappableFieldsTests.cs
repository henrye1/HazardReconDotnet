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

    [Fact]
    public void TestAgeAnalysisListsItsThreeFields()
    {
        Assert.Equal(
            new[] { "LoanAccountNumber", "TransactionNumber", "AgingBuckets" },
            MappableFields.AgeAnalysis.Select(f => f.Field));
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
            MappableFields.Writeoff.Concat(MappableFields.Exposure).Concat(MappableFields.AgeAnalysis),
            f => Assert.False(string.IsNullOrWhiteSpace(f.Note)));
    }
}
