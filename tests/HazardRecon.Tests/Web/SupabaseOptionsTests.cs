using HazardRecon.Web.Supabase;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseOptionsTests
{
    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co",
        AnonKey = "anon-key",
        ServiceRoleKey = "service-key"
    };

    [Fact]
    public void TestFullyPopulatedOptionsAreConfigured()
    {
        Assert.True(Options().IsConfigured);
        Assert.Empty(Options().MissingKeys());
    }

    [Fact]
    public void TestBlankUrlIsNotConfigured()
    {
        SupabaseOptions o = Options();
        o.Url = "   ";

        Assert.False(o.IsConfigured);
        Assert.Contains("Supabase:Url", o.MissingKeys());
    }

    [Fact]
    public void TestBlankServiceRoleKeyIsNotConfigured()
    {
        SupabaseOptions o = Options();
        o.ServiceRoleKey = "";

        Assert.False(o.IsConfigured);
        Assert.Contains("Supabase:ServiceRoleKey", o.MissingKeys());
    }

    [Fact]
    public void TestMissingKeysNamesEveryBlankField()
    {
        SupabaseOptions o = new();

        Assert.Equal(
            new[] { "Supabase:Url", "Supabase:AnonKey", "Supabase:ServiceRoleKey" },
            o.MissingKeys());
    }

    [Fact]
    public void TestTrailingSlashIsStrippedFromUrl()
    {
        SupabaseOptions o = Options();
        o.Url = "https://ref.supabase.co/";

        Assert.Equal("https://ref.supabase.co", o.BaseUrl);
    }
}
