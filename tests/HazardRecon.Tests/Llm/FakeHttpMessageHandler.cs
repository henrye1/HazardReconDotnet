using System.Net;
using System.Text;

namespace HazardRecon.Tests.Llm;

/// <summary>
/// Records every outbound request and answers from a caller-supplied responder.
/// The responder receives the request and its zero-based index, so a test can
/// return different responses on successive calls (e.g. 401 then 200).
/// </summary>
internal class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, (HttpStatusCode Status, string Body)> _responder;

    public List<(string Method, string Url, string Body)> Requests { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, int, (HttpStatusCode Status, string Body)> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;
        int index = Requests.Count;
        Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body));

        (HttpStatusCode status, string responseBody) = _responder(request, index);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
        };
    }
}
