using System.Buffers;
using System.Text;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

/// <summary>
/// Turns the bytes of a <c>text/event-stream</c> body into the data of its events, following the
/// server-sent events framing rules as far as a chat completion stream needs them.
/// </summary>
/// <remarks>
/// <para>
/// Bytes arrive in whatever pieces the network delivers, so nothing here assumes a read ends on a
/// line, an event or even a character: a UTF-8 sequence split across reads is completed by the
/// decoder, and a CR that ends one read and an LF that starts the next are one line break. Lines end
/// with LF, CRLF or CR. A byte order mark at the very start of the stream is dropped.
/// </para>
/// <para>
/// Only the <c>data</c> field is kept. Its lines are joined with LF and the event is dispatched at the
/// blank line that ends it; an event whose data is empty is not dispatched. <c>event</c>, <c>id</c>,
/// <c>retry</c> and unknown fields are ignored, as are comment lines starting with a colon, which is
/// how keep-alives are sent. Nothing is dispatched for an event the stream never finished, so a body
/// that stops mid-event yields no partial data. A line or an event longer than the cap ends the body
/// as too large rather than being buffered further.
/// </para>
/// </remarks>
internal sealed class OpenAiServerSentEventParser(
    int maxEventCharacters = OpenAiServerSentEventParser.DefaultMaxEventCharacters)
{
    /// <summary>
    /// The longest line, and the longest joined data of one event, in characters: 4 MiB. A streamed
    /// chunk carries one token or tool-call fragment in a few hundred bytes, so this is far beyond any
    /// legitimate event, and it keeps a hostile line from being buffered up to the whole body limit.
    /// </summary>
    public const int DefaultMaxEventCharacters = 4 * 1024 * 1024;

    private const char ByteOrderMark = '\uFEFF';

    private readonly Decoder decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();
    private readonly StringBuilder line = new();
    private readonly StringBuilder data = new();
    private bool hasData;
    private bool previousWasCarriageReturn;
    private bool atStreamStart = true;

    /// <summary>Consumes the next bytes of the body.</summary>
    /// <param name="bytes">The bytes one read delivered.</param>
    /// <param name="dispatched">Receives the data of every event these bytes completed, in order.</param>
    public void Append(ReadOnlySpan<byte> bytes, List<string> dispatched)
    {
        // GetChars runs even when these bytes complete no character: it is the call that keeps the
        // start of a split UTF-8 sequence in the decoder for the next read.
        var charCount = decoder.GetCharCount(bytes, flush: false);
        var chars = ArrayPool<char>.Shared.Rent(Math.Max(1, charCount));
        try
        {
            var decoded = decoder.GetChars(bytes, chars, flush: false);
            foreach (var character in chars.AsSpan(0, decoded))
            {
                Consume(character, dispatched);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private void Consume(char character, List<string> dispatched)
    {
        if (atStreamStart)
        {
            atStreamStart = false;
            if (character == ByteOrderMark)
            {
                return;
            }
        }

        if (character == '\n')
        {
            if (previousWasCarriageReturn)
            {
                previousWasCarriageReturn = false;
                return;
            }

            EndLine(dispatched);
            return;
        }

        if (character == '\r')
        {
            EndLine(dispatched);
            previousWasCarriageReturn = true;
            return;
        }

        previousWasCarriageReturn = false;
        if (line.Length >= maxEventCharacters)
        {
            throw new OpenAiResponseBodyTooLargeException(maxEventCharacters);
        }

        line.Append(character);
    }

    private void EndLine(List<string> dispatched)
    {
        if (line.Length == 0)
        {
            DispatchEvent(dispatched);
            return;
        }

        if (line[0] == ':')
        {
            line.Clear();
            return;
        }

        var text = line.ToString();
        line.Clear();
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var field = colon < 0 ? text : text[..colon];
        if (!string.Equals(field, "data", StringComparison.Ordinal))
        {
            return;
        }

        var value = colon < 0 ? string.Empty : text[(colon + 1)..];
        if (value.StartsWith(' '))
        {
            value = value[1..];
        }

        if (data.Length + value.Length + 1 > maxEventCharacters)
        {
            throw new OpenAiResponseBodyTooLargeException(maxEventCharacters);
        }

        if (hasData)
        {
            data.Append('\n');
        }

        data.Append(value);
        hasData = true;
    }

    private void DispatchEvent(List<string> dispatched)
    {
        if (hasData && data.Length > 0)
        {
            dispatched.Add(data.ToString());
        }

        data.Clear();
        hasData = false;
    }
}
