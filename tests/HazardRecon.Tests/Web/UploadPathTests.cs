using HazardRecon.Web.Uploads;
using Xunit;

namespace HazardRecon.Tests.Web;

/// <summary>
/// webkitRelativePath comes straight from the browser and is fully
/// attacker-controlled, so it is normalised and rejected here before it is ever
/// used as a filesystem path.
/// </summary>
public class UploadPathTests
{
    [Theory]
    [InlineData("debug.zip", "debug.zip")]
    [InlineData("SET A/debug.zip", "SET A/debug.zip")]
    [InlineData("SET A\\debug.zip", "SET A/debug.zip")]
    [InlineData("./SET A/debug.zip", "SET A/debug.zip")]
    [InlineData("SET A//debug.zip", "SET A/debug.zip")]
    [InlineData("a/b/c/lgd_defaults.csv", "a/b/c/lgd_defaults.csv")]
    public void TestAcceptableRelativePathsAreNormalised(string input, string expected)
    {
        Assert.True(UploadPath.TryNormalize(input, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("a/../../secrets.txt")]
    [InlineData("a/b/../../../etc/passwd")]
    [InlineData("..\\secrets.txt")]
    [InlineData("a\\..\\..\\secrets.txt")]
    public void TestParentSegmentsAreRejected(string input)
    {
        Assert.False(UploadPath.TryNormalize(input, out _));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\file.csv")]
    [InlineData("C:/Windows/System32/config")]
    [InlineData("C:\\Windows\\System32\\config")]
    [InlineData("d:file.csv")]
    public void TestAbsolutePathsAreRejected(string input)
    {
        Assert.False(UploadPath.TryNormalize(input, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("/")]
    [InlineData("./")]
    public void TestEmptyOrMeaninglessPathsAreRejected(string input)
    {
        Assert.False(UploadPath.TryNormalize(input, out _));
    }

    [Fact]
    public void TestNullIsRejected()
    {
        Assert.False(UploadPath.TryNormalize(null, out _));
    }

    [Fact]
    public void TestNulByteIsRejected()
    {
        // truncates the path at the OS layer, so a rejected suffix can vanish
        Assert.False(UploadPath.TryNormalize("good.csv\0../../evil", out _));
    }

    [Theory]
    [InlineData("bad<name.csv")]
    [InlineData("bad>name.csv")]
    [InlineData("bad|name.csv")]
    [InlineData("bad?name.csv")]
    [InlineData("bad*name.csv")]
    [InlineData("bad\"name.csv")]
    [InlineData("folder/bad:name.csv")]
    public void TestSegmentsWithIllegalFilenameCharactersAreRejected(string input)
    {
        // rejected here rather than throwing deep inside Directory.CreateDirectory
        Assert.False(UploadPath.TryNormalize(input, out _));
    }

    [Fact]
    public void TestControlCharactersAreRejected()
    {
        Assert.False(UploadPath.TryNormalize("a/badname.csv", out _));
    }

    [Fact]
    public void TestALeadingDotFolderIsKept()
    {
        // "_extracted" and similar are real, only ".." is dangerous
        Assert.True(UploadPath.TryNormalize("_extracted/lgd_defaults.csv", out string n));
        Assert.Equal("_extracted/lgd_defaults.csv", n);
    }

    [Fact]
    public void TestTheNormalisedPathNeverEscapesItsRoot()
    {
        // the property that actually matters, stated directly
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hr-root-test"));

        foreach (string candidate in new[]
                 { "a/b.csv", "SET A\\debug.zip", "./x/y/z.csv", "_extracted/lgd_defaults.csv" })
        {
            Assert.True(UploadPath.TryNormalize(candidate, out string normalized));

            string combined = Path.GetFullPath(Path.Combine(root, normalized));
            Assert.StartsWith(root + Path.DirectorySeparatorChar, combined, StringComparison.OrdinalIgnoreCase);
        }
    }
}
