using System.Net;
using System.Text;

namespace CodeIndex.Tests;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<RecordedHttpRequest, CancellationToken, Task<HttpResponseMessage>>> responses = new();
    private readonly List<RecordedHttpRequest> requests = [];

    internal IReadOnlyList<RecordedHttpRequest> Requests => requests;

    internal int RequestCount => requests.Count;

    internal void QueueResponse(HttpResponseMessage response)
        => responses.Enqueue((_, _) => Task.FromResult(response));

    internal void QueueJson(HttpStatusCode statusCode, string json)
        => QueueResponse(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    internal void QueueException(Exception exception)
        => responses.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(exception));

    internal void QueueTimeout()
        => responses.Enqueue(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var recorded = await RecordedHttpRequest.FromAsync(request, cancellationToken).ConfigureAwait(false);
        requests.Add(recorded);

        if (responses.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }

        return await responses.Dequeue()(recorded, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record RecordedHttpRequest(
    HttpMethod Method,
    Uri? RequestUri,
    IReadOnlyDictionary<string, string[]> Headers,
    string? AuthorizationScheme,
    string? AuthorizationParameter,
    string? Body)
{
    internal static async Task<RecordedHttpRequest> FromAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(
            header => header.Key,
            header => header.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        string? body = null;
        if (request.Content != null)
            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return new RecordedHttpRequest(
            request.Method,
            request.RequestUri,
            headers,
            request.Headers.Authorization?.Scheme,
            request.Headers.Authorization?.Parameter,
            body);
    }

    internal bool HasHeaderValue(string name, string value)
        => Headers.TryGetValue(name, out var values)
            && values.Any(candidate => string.Equals(candidate, value, StringComparison.Ordinal));
}
