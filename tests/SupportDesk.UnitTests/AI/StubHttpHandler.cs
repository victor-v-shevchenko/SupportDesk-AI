using System.Net;
using System.Text;

namespace SupportDesk.UnitTests.AI;

/// <summary>Answers every request with one canned response and keeps the request bodies, so the real SDK adapters run without a network.</summary>
internal sealed class StubHttpHandler(HttpStatusCode status, string body, string contentType = "application/json") : HttpMessageHandler
{
    private readonly List<string> _requests = [];

    public IReadOnlyList<string> Requests => _requests;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
    }
}
