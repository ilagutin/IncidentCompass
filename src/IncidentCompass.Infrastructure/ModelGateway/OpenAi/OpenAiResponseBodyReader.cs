using System.Buffers;
using System.Text;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

/// <summary>
/// Reads a provider response body under an inactivity limit rather than a total one.
/// </summary>
/// <remarks>
/// The limit restarts every time the body delivers bytes, so a body that keeps arriving is never cut
/// off for being long, and a body that stops arriving is abandoned once it has been silent for the
/// configured time.
/// </remarks>
internal static class OpenAiResponseBodyReader
{
    private const int ChunkSize = 16 * 1024;

    /// <summary>Reads the whole body as text.</summary>
    /// <param name="content">The response content.</param>
    /// <param name="inactivity">
    /// The inactivity timer, owned by the caller so it can tell after a cancellation whether this
    /// limit is what fired.
    /// </param>
    /// <param name="inactivityLimit">How long the body may deliver nothing.</param>
    /// <param name="maxBytes">
    /// The largest body accepted. The caller passes the HTTP client's
    /// <see cref="HttpClient.MaxResponseContentBufferSize" />, the limit the client applied itself when
    /// it buffered the whole response, so reading incrementally does not remove that bound.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    public static async Task<string> ReadAsync(
        HttpContent content,
        CancellationTokenSource inactivity,
        TimeSpan inactivityLimit,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, inactivity.Token);
        inactivity.CancelAfter(inactivityLimit);

        await using var stream = await content.ReadAsStreamAsync(linked.Token);
        using var body = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(0, ChunkSize), linked.Token)) > 0)
            {
                if (body.Length + read > maxBytes)
                {
                    throw new OpenAiResponseBodyTooLargeException(maxBytes);
                }

                body.Write(chunk, 0, read);
                inactivity.CancelAfter(inactivityLimit);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        body.Position = 0;
        using var reader = new StreamReader(
            body,
            ResolveEncoding(content.Headers.ContentType?.CharSet),
            detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>
    /// The declared charset when it names a known encoding, otherwise UTF-8, which is what a JSON
    /// body is required to use when it declares nothing.
    /// </summary>
    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
