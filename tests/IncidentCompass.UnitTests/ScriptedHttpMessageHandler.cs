using System.Net;

namespace IncidentCompass.UnitTests;

/// <summary>
/// An HTTP handler that answers from a script and records every URI it was asked for, so a store
/// test can assert both what was downloaded and that nothing was.
/// </summary>
internal sealed class ScriptedHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly List<Uri> requestedUris = [];

    public IReadOnlyList<Uri> RequestedUris
    {
        get
        {
            lock (requestedUris)
            {
                return requestedUris.ToArray();
            }
        }
    }

    public static ScriptedHttpMessageHandler Serving(IReadOnlyDictionary<string, byte[]> bodies) =>
        new((request, _) => Task.FromResult(
            bodies.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound)));

    /// <summary>A handler for a test that expects no download at all; any request fails the test.</summary>
    public static ScriptedHttpMessageHandler Refusing() =>
        new((request, _) => throw new InvalidOperationException(
            "This test expects no download, but " + request.RequestUri + " was requested."));

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (requestedUris)
        {
            requestedUris.Add(request.RequestUri!);
        }

        return respond(request, cancellationToken);
    }
}
