using System.Net;
using System.Text;
using HazardRecon.Tests.Llm;
using HazardRecon.Web.Files;
using HazardRecon.Web.Supabase;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseFileStoreTests
{
    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co",
        AnonKey = "anon-key",
        ServiceRoleKey = "service-key"
    };

    private static SupabaseFileStore Store(FakeHttpMessageHandler handler) =>
        new(new SupabaseRestClient(Options(), handler), Options());

    [Fact]
    public async Task TestUploadPostsToTheObjectPath()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "{}"));
        using MemoryStream content = new(Encoding.UTF8.GetBytes("col_a,col_b\n1,2\n"));

        await Store(handler).UploadAsync("user/run/output/report.csv", content, "text/csv");

        Assert.Equal("POST", handler.Requests[0].Method);
        Assert.Equal("https://ref.supabase.co/storage/v1/object/runs/user/run/output/report.csv",
            handler.Requests[0].Url);
        Assert.Contains("col_a,col_b", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TestSignedUrlIsReturnedAbsolute()
    {
        FakeHttpMessageHandler handler = new((_, _) =>
            (HttpStatusCode.OK, """{"signedURL":"/object/sign/runs/user/run/output/report.csv?token=abc"}"""));

        string url = await Store(handler).CreateSignedUrlAsync("user/run/output/report.csv", 60);

        Assert.Equal(
            "https://ref.supabase.co/storage/v1/object/sign/runs/user/run/output/report.csv?token=abc",
            url);
    }

    [Fact]
    public async Task TestSignedUrlRequestCarriesTheExpiry()
    {
        FakeHttpMessageHandler handler = new((_, _) =>
            (HttpStatusCode.OK, """{"signedURL":"/object/sign/runs/x?token=t"}"""));

        await Store(handler).CreateSignedUrlAsync("x", 60);

        Assert.Equal("https://ref.supabase.co/storage/v1/object/sign/runs/x", handler.Requests[0].Url);
        Assert.Contains("\"expiresIn\":60", handler.Requests[0].Body);
    }

    [Fact]
    public async Task TestDeletePrefixListsThenDeletesEveryObjectFound()
    {
        FakeHttpMessageHandler handler = new((_, i) =>
            i == 0
                ? (HttpStatusCode.OK, """[{"name":"a.csv"},{"name":"b.csv"}]""")
                : (HttpStatusCode.OK, "[]"));

        await Store(handler).DeletePrefixAsync("user/run/input");

        Assert.Equal("https://ref.supabase.co/storage/v1/object/list/runs", handler.Requests[0].Url);
        Assert.Contains("user/run/input", handler.Requests[0].Body);

        Assert.Equal("DELETE", handler.Requests[1].Method);
        Assert.Contains("user/run/input/a.csv", handler.Requests[1].Body);
        Assert.Contains("user/run/input/b.csv", handler.Requests[1].Body);
    }

    [Fact]
    public async Task TestDeletePrefixSkipsTheDeleteWhenNothingMatches()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, "[]"));

        await Store(handler).DeletePrefixAsync("user/run/input");

        Assert.Single(handler.Requests);
    }
}
