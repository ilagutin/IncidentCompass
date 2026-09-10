namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// How patch text is cut into lines, and the one place a carriage return is allowed to be ignored.
/// </summary>
/// <remarks>
/// The patch is split on line feeds alone and a carriage return is never stripped from a body line,
/// because a body line carries the file's exact bytes: a patch generated against a file with
/// Windows line endings must only match a file with Windows line endings, and stripping here would
/// let it match either. Structural lines are different. A header, a hunk header or a no-newline
/// marker carries no file bytes, so a trailing carriage return on one is a property of how the patch
/// was transported rather than of the base it applies to, and <see cref="TrimStructural"/> is the
/// only place that difference is allowed to matter.
/// </remarks>
internal static class SourcePatchText
{
    public const string NoNewlineMarker = @"\ No newline at end of file";

    /// <summary>
    /// Cuts the patch into lines on line feeds, dropping one terminator at the very end so a patch
    /// that ends with a newline does not read as ending with a blank line.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string patch)
    {
        var lines = patch.Split('\n');
        return lines[^1].Length == 0 ? lines[..^1] : lines;
    }

    /// <summary>Removes one trailing carriage return from a structural line. Never called on a body line.</summary>
    public static string TrimStructural(string line) =>
        line.Length > 0 && line[^1] == '\r' ? line[..^1] : line;

    /// <summary>
    /// Parses one non-negative decimal field of a hunk header. A sign, whitespace, a group separator
    /// or anything else is not a number here, so the header is malformed rather than coerced.
    /// </summary>
    public static bool TryParseCount(ReadOnlySpan<char> value, out int parsed) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parsed);
}
