using System.Text;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// A file's bytes seen as the lines a hunk addresses, and back again without losing a byte.
/// </summary>
/// <param name="Lines">
/// The file cut on line feeds, each line keeping any carriage return that preceded its terminator.
/// </param>
/// <param name="EndsWithNewline">
/// Whether the last line carried a terminator. It is carried separately because the difference is
/// invisible in a list of lines and visible in the file's bytes, and the tree identity is over
/// bytes.
/// </param>
/// <remarks>
/// <see cref="TryDecode"/> and <see cref="TryRender"/> round-trip exactly: decoding a file and
/// rendering it unchanged reproduces the original bytes, byte-order mark included. That is the
/// property the applier depends on, because a file the patch does not change must not change, and a
/// file it does change must differ only where the patch said.
/// </remarks>
internal sealed record SourceTextFile(IReadOnlyList<string> Lines, bool EndsWithNewline)
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes a file as strict UTF-8 text, or returns <c>null</c> when it is not text this can
    /// address by line: a NUL byte or an invalid UTF-8 sequence means the bytes are not lines, and
    /// applying a text hunk to them would corrupt the file rather than patch it. This is the same
    /// test the excerpt reader applies, so a file this refuses is a file the evidence path also
    /// refuses.
    /// </summary>
    public static SourceTextFile? TryDecode(byte[] content)
    {
        if (content.AsSpan().Contains((byte)0))
        {
            return null;
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        if (text.Length == 0)
        {
            return new SourceTextFile([], EndsWithNewline: false);
        }

        var lines = text.Split('\n');
        return lines[^1].Length == 0
            ? new SourceTextFile(lines[..^1], EndsWithNewline: true)
            : new SourceTextFile(lines, EndsWithNewline: false);
    }

    /// <summary>
    /// Renders lines back to bytes, or returns <c>null</c> when they are not bytes. A file with no
    /// lines renders to no bytes rather than to a lone terminator, so a patch that removes every
    /// line leaves an empty file and not a blank one.
    /// </summary>
    /// <remarks>
    /// A line can come from the patch rather than from a decoded file, and a patch is a UTF-16
    /// string that may hold a surrogate with no partner. Such a string denotes no character, so
    /// there is nothing to encode and no file that could hold it. The parser refuses one in the
    /// patch text, which is where the refusal is reproducible from the text alone; this returns
    /// <c>null</c> rather than throwing so that a caller reaching it another way gets a refusal
    /// instead of an exception the applier would have to guess the meaning of.
    /// </remarks>
    public static byte[]? TryRender(IReadOnlyList<string> lines, bool endsWithNewline)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        var text = string.Join('\n', lines) + (endsWithNewline ? "\n" : string.Empty);
        try
        {
            return StrictUtf8.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }
}
