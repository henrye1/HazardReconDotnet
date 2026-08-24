using HazardRecon.Core.Models;
using Xunit;

namespace HazardRecon.Tests;

public class ColumnMapTests
{
    [Fact]
    public void TestResolveReturnsTheMappedColumnWhenPresent()
    {
        ColumnMap map = new(hasHeaders: true, new Dictionary<string, string> { ["LoanAccountNumber"] = "Column 1" });

        Assert.Equal("Column 1", map.Resolve("LoanAccountNumber"));
    }

    [Fact]
    public void TestResolveFallsBackToTheFieldNameWhenNotMapped()
    {
        ColumnMap map = new(hasHeaders: true, new Dictionary<string, string>());

        Assert.Equal("Amount", map.Resolve("Amount"));
    }

    [Fact]
    public void TestResolveAllReturnsEveryColumnInThePickedOrder()
    {
        ColumnMap map = new(hasHeaders: true, new Dictionary<string, IReadOnlyList<string>>
        {
            ["AgingBuckets"] = new[] { "60 Days", "90 Days" }
        });

        Assert.Equal(new[] { "60 Days", "90 Days" }, map.ResolveAll("AgingBuckets"));
        // Resolve on a multi-valued field hands back the first, which is why only
        // the loaders that sum should be calling ResolveAll
        Assert.Equal("60 Days", map.Resolve("AgingBuckets"));
    }

    [Fact]
    public void TestResolveAllFallsBackToTheFieldNameWhenNotMapped()
    {
        ColumnMap map = new(hasHeaders: true, new Dictionary<string, IReadOnlyList<string>>());

        Assert.Equal(new[] { "AmountOutstanding" }, map.ResolveAll("AmountOutstanding"));
    }

    /// <summary>
    /// "The user picked nothing" is not "nobody was asked": an entry that is
    /// present but empty must stay empty, so the loader can refuse it rather than
    /// silently reading a column named after the field.
    /// </summary>
    [Fact]
    public void TestAnExplicitlyEmptySelectionStaysEmpty()
    {
        ColumnMap map = new(hasHeaders: true, new Dictionary<string, IReadOnlyList<string>>
        {
            ["AgingBuckets"] = Array.Empty<string>()
        });

        Assert.Empty(map.ResolveAll("AgingBuckets"));
    }

    [Fact]
    public void TestTheSingleColumnFormStillWorksThroughResolveAll()
    {
        ColumnMap map = new(hasHeaders: true, new Dictionary<string, string> { ["Amount"] = "Value" });

        Assert.Equal("Value", map.Resolve("Amount"));
        Assert.Equal(new[] { "Value" }, map.ResolveAll("Amount"));
    }

    [Fact]
    public void TestHasHeadersIsExposed()
    {
        ColumnMap headerless = new(hasHeaders: false, new Dictionary<string, string>());

        Assert.False(headerless.HasHeaders);
    }
}
