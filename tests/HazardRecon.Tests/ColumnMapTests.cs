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
    public void TestHasHeadersIsExposed()
    {
        ColumnMap headerless = new(hasHeaders: false, new Dictionary<string, string>());

        Assert.False(headerless.HasHeaders);
    }
}
