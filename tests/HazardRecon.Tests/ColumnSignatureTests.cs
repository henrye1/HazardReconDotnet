using HazardRecon.Core.Services;
using Xunit;

namespace HazardRecon.Tests;

public class ColumnSignatureTests
{
    private static IReadOnlyList<IReadOnlyList<string>> Rows(params string[][] rows) =>
        rows.Select(r => (IReadOnlyList<string>)r).ToList();

    [Fact]
    public void TestSameHeadersProduceTheSameSignatureRegardlessOfCase()
    {
        string a = ColumnSignature.Compute(new[] { "LoanAccountNumber", "Amount" }, Rows());
        string b = ColumnSignature.Compute(new[] { "loanaccountnumber", "AMOUNT" }, Rows());

        Assert.Equal(a, b);
    }

    [Fact]
    public void TestDifferentHeaderOrderProducesADifferentSignature()
    {
        string a = ColumnSignature.Compute(new[] { "LoanAccountNumber", "Amount" }, Rows());
        string b = ColumnSignature.Compute(new[] { "Amount", "LoanAccountNumber" }, Rows());

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TestHeaderlessFilesWithTheSameColumnShapesMatch()
    {
        string a = ColumnSignature.Compute(null, Rows(
            new[] { "A1", "2026-04-30", "100.50" },
            new[] { "A2", "2026-05-01", "200.75" }));

        string b = ColumnSignature.Compute(null, Rows(
            new[] { "B9", "2026-06-15", "999.00" },
            new[] { "B8", "2026-06-16", "1.00" }));

        Assert.Equal(a, b);
    }

    [Fact]
    public void TestHeaderlessFilesWithDifferentColumnShapesDoNotMatch()
    {
        string numericThenDate = ColumnSignature.Compute(null, Rows(new[] { "100", "2026-04-30" }));
        string dateThenNumeric = ColumnSignature.Compute(null, Rows(new[] { "2026-04-30", "100" }));

        Assert.NotEqual(numericThenDate, dateThenNumeric);
    }

    [Fact]
    public void TestAHeaderedSignatureNeverMatchesAHeaderlessOne()
    {
        string headered = ColumnSignature.Compute(new[] { "A", "B" }, Rows());
        string headerless = ColumnSignature.Compute(null, Rows(new[] { "A", "B" }));

        Assert.NotEqual(headered, headerless);
    }
}
