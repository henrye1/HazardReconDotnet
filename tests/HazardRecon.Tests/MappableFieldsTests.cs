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
    public void TestEveryFieldHasANonEmptyNote()
    {
        Assert.All(MappableFields.Writeoff.Concat(MappableFields.Exposure),
            f => Assert.False(string.IsNullOrWhiteSpace(f.Note)));
    }
}
