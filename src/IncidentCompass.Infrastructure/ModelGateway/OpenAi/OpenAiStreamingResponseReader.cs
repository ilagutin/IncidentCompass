using System.Buffers;
using System.Text.Json;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi.Dtos;
using IncidentCompass.Infrastructure.OpenAiCompatible;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

/// <summary>
/// Reads a streamed chat completion, a <c>text/event-stream</c> body, under the first-output and
/// inactivity limits, and assembles it into one completion.
/// </summary>
/// <remarks>
/// <para>
/// Output is a <c>data</c> event. Until the first one arrives the first-output timer, armed at
/// dispatch, stays in force; the event disarms it and later reads no longer listen to it. From then on the inactivity timer restarts on
/// every <c>data</c> event, whatever the event carries, so a model that is only reasoning or streaming
/// a tool call is still producing. Keep-alive comments and blank lines restart nothing: they show that
/// the connection is open, not that the model is producing anything. The size limit counts every byte
/// read, comments included, and no single line or event may exceed its own cap.
/// </para>
/// <para>
/// <c>data: [DONE]</c> ends the stream and nothing after it is read. Whether the body ends with it or
/// simply closes, the answer is accepted only when choice <c>0</c> has reported a finish reason;
/// otherwise it was cut off. <c>[DONE]</c> before any choice is left to the mapping as an empty answer.
/// A <c>data</c> event that is not a JSON object is invalid JSON, and an event carrying an
/// <c>error</c> ends the call as a provider failure after dispatch.
/// </para>
/// </remarks>
internal static class OpenAiStreamingResponseReader
{
    public const string EventStreamMediaType = "text/event-stream";

    private const string DoneMarker = "[DONE]";
    private const int ChunkSize = 16 * 1024;

    public static bool IsEventStream(HttpContent content)
    {
        return string.Equals(
            content.Headers.ContentType?.MediaType,
            EventStreamMediaType,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <param name="content">The successful response's content.</param>
    /// <param name="firstOutput">The first-output timer, armed at dispatch and owned by the caller.</param>
    /// <param name="inactivity">The inactivity timer, unarmed until output starts, owned by the caller.</param>
    /// <param name="inactivityLimit">How long the stream may go without a <c>data</c> event.</param>
    /// <param name="maxBytes">The largest number of body bytes read.</param>
    /// <param name="onOutputStarted">
    /// Called once, when the first <c>data</c> event arrived and the first-output timer was disarmed
    /// before it fired, so the caller can attribute a later cancellation to the right limit.
    /// </param>
    /// <param name="cancellationToken">The caller's cancellation.</param>
    public static async Task<OpenAiChatCompletionResponse> ReadAsync(
        HttpContent content,
        CancellationTokenSource firstOutput,
        CancellationTokenSource inactivity,
        TimeSpan inactivityLimit,
        long maxBytes,
        Action onOutputStarted,
        CancellationToken cancellationToken)
    {
        // Until output starts a read may be ended by the caller, the first-output timer or (never, as
        // it is still unarmed) the inactivity timer. Once the first data event arrived, later reads
        // are linked to the caller and the inactivity timer only. Disarming the first-output timer is
        // not enough on its own: a timer callback already running when the event lands would still
        // cancel a live stream through a token the reads kept listening to, and that cancellation
        // would be attributed to no limit. The manual clock in the unit tests fires timers
        // synchronously inside Advance, so it cannot place a callback between the disarm and the
        // cancellation; the re-link is what closes that window rather than a test.
        var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            firstOutput.Token,
            inactivity.Token);
        var parser = new OpenAiServerSentEventParser();
        var accumulator = new OpenAiStreamingCompletionAccumulator();
        var events = new List<string>();
        var outputStarted = false;
        long bytesRead = 0;
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(readCancellation.Token);
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(0, ChunkSize), readCancellation.Token)) > 0)
            {
                bytesRead += read;
                if (bytesRead > maxBytes)
                {
                    throw new OpenAiResponseBodyTooLargeException(maxBytes);
                }

                events.Clear();
                parser.Append(chunk.AsSpan(0, read), events);
                foreach (var data in events)
                {
                    if (!outputStarted)
                    {
                        DisarmFirstOutput(firstOutput);
                        readCancellation.Dispose();
                        readCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            inactivity.Token);
                        outputStarted = true;
                        onOutputStarted();
                    }

                    inactivity.CancelAfter(inactivityLimit);
                    if (string.Equals(data.Trim(), DoneMarker, StringComparison.Ordinal))
                    {
                        return CompleteAtDone(accumulator);
                    }

                    accumulator.Append(ParseChunk(data));
                }
            }
        }
        finally
        {
            readCancellation.Dispose();
            ArrayPool<byte>.Shared.Return(chunk);
        }

        if (!accumulator.HasFinishReason)
        {
            throw OpenAiModelErrorMapper.StreamTruncated();
        }

        return accumulator.ToResponse();
    }

    /// <summary>
    /// <c>[DONE]</c> ends a finished answer only when choice <c>0</c> reported why it finished;
    /// <c>[DONE]</c> after a choice that never finished is a cut-off answer. <c>[DONE]</c> before any
    /// choice is left to the response mapping, which refuses it as an empty answer.
    /// </summary>
    private static OpenAiChatCompletionResponse CompleteAtDone(OpenAiStreamingCompletionAccumulator accumulator)
    {
        if (accumulator.HasChoice && !accumulator.HasFinishReason)
        {
            throw OpenAiModelErrorMapper.StreamTruncated();
        }

        return accumulator.ToResponse();
    }

    /// <summary>
    /// Stops the first-output timer. When it fired before it could be stopped, that limit is what
    /// ended the attempt, and the cancellation is raised before output is recorded as started.
    /// </summary>
    private static void DisarmFirstOutput(CancellationTokenSource firstOutput)
    {
        firstOutput.CancelAfter(Timeout.InfiniteTimeSpan);
        firstOutput.Token.ThrowIfCancellationRequested();
    }

    private static OpenAiChatCompletionChunk ParseChunk(string data)
    {
        return JsonSerializer.Deserialize<OpenAiChatCompletionChunk>(data, OpenAiCompatibleJson.Options)
               ?? throw new JsonException("A stream data event was the JSON literal null.");
    }
}
