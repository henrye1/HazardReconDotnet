using System.Text.Json;
using HazardRecon.Web.Runs;
using Xunit;

namespace HazardRecon.Tests.Web;

public class RunFileRecordTests
{
    [Fact]
    public void TestRoleAndOriginalNameRoundTripThroughJson()
    {
        RunFileRecord record = new()
        {
            Kind = "input",
            RelativePath = "0/IFRS9.csv",
            StoragePath = "u/r/input/0/IFRS9.csv",
            SizeBytes = 12,
            Role = "exposure",
            OriginalName = "IFRS9 FILE JUNE 2025.csv"
        };

        string json = JsonSerializer.Serialize(record);
        Assert.Contains("\"role\":\"exposure\"", json);
        Assert.Contains("\"original_name\":\"IFRS9 FILE JUNE 2025.csv\"", json);

        RunFileRecord back = JsonSerializer.Deserialize<RunFileRecord>(json)!;
        Assert.Equal("exposure", back.Role);
        Assert.Equal("IFRS9 FILE JUNE 2025.csv", back.OriginalName);
    }

    [Fact]
    public void TestRowsWrittenBeforeTheMigrationDeserialiseWithBothNull()
    {
        // a row from an older run: the columns did not exist when it was written
        string json = """
        {"kind":"input","relative_path":"0/writeoff.csv","storage_path":"u/r/input/0/writeoff.csv","size_bytes":9}
        """;

        RunFileRecord back = JsonSerializer.Deserialize<RunFileRecord>(json)!;

        Assert.Null(back.Role);
        Assert.Null(back.OriginalName);
        Assert.Equal("0/writeoff.csv", back.RelativePath);
    }
}
