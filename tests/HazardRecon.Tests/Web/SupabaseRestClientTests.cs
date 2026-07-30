using System.Net;
using System.Text;
using HazardRecon.Tests.Llm;
using HazardRecon.Web.Supabase;
using Xunit;

namespace HazardRecon.Tests.Web;

public class SupabaseRestClientTests
{
    private static SupabaseOptions Options() => new()
    {
        Url = "https://ref.supabase.co",
        AnonKey = "anon-key",
        ServiceRoleKey = "service-key"
    };

    [Fact]
    public async Task TestEveryRequestCarriesTheServiceRoleKey()
    {
        string? apikey = null;
        string? auth = null;
        FakeHttpMessageHandler handler = new((req, _) =>
        {
            apikey = req.Headers.TryGetValues("apikey", out var a) ? string.Join(",", a) : null;
            auth = req.Headers.Authorization?.ToString();
            return (HttpStatusCode.OK, "[]");
        });
        SupabaseRestClient client = new(Options(), handler);

        await client.SendAsync(HttpMethod.Get, "/rest/v1/runs");

        Assert.Single(handler.Requests);
        Assert.Equal("https://ref.supabase.co/rest/v1/runs", handler.Requests[0].Url);
        Assert.Equal("service-key", apikey);
        Assert.Equal("Bearer service-key", auth);
    }

    [Fact]
    public async Task TestTheBodyIsReturnedVerbatim()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.OK, """[{"id":"abc"}]"""));
        SupabaseRestClient client = new(Options(), handler);

        string body = await client.SendAsync(HttpMethod.Get, "/rest/v1/runs");

        Assert.Equal("""[{"id":"abc"}]""", body);
    }

    [Fact]
    public async Task TestExtraHeadersAreSent()
    {
        string? prefer = null;
        FakeHttpMessageHandler handler = new((req, _) =>
        {
            prefer = req.Headers.TryGetValues("Prefer", out var v) ? string.Join(",", v) : null;
            return (HttpStatusCode.OK, "[]");
        });
        SupabaseRestClient client = new(Options(), handler);

        await client.SendAsync(HttpMethod.Post, "/rest/v1/runs",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            new Dictionary<string, string> { ["Prefer"] = "return=representation" });

        Assert.Equal("return=representation", prefer);
    }

    [Fact]
    public async Task TestNon2xxThrowsWithTheStatusAndBody()
    {
        FakeHttpMessageHandler handler = new((_, _) =>
            (HttpStatusCode.BadRequest, """{"message":"bad column"}"""));
        SupabaseRestClient client = new(Options(), handler);

        SupabaseException ex = await Assert.ThrowsAsync<SupabaseException>(
            () => client.SendAsync(HttpMethod.Get, "/rest/v1/runs"));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("bad column", ex.Message);
    }

    [Fact]
    public async Task TestTheServiceRoleKeyIsNeverInTheExceptionMessage()
    {
        FakeHttpMessageHandler handler = new((_, _) => (HttpStatusCode.Forbidden, "denied"));
        SupabaseRestClient client = new(Options(), handler);

        SupabaseException ex = await Assert.ThrowsAsync<SupabaseException>(
            () => client.SendAsync(HttpMethod.Get, "/rest/v1/runs"));

        Assert.DoesNotContain("service-key", ex.Message);
    }
}
