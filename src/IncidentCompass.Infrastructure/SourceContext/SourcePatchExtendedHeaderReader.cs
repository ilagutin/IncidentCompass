namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Reads the extended headers a git-format diff may put between <c>diff --git</c> and <c>---</c>,
/// and refuses every operation they can express that this applier does not implement.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that an unsupported operation is refused by name rather than ignored. A parser
/// that skipped unknown header lines would read a rename as a plain modification of the destination
/// and a mode change as no change at all, which turns "we do not support that" into "we quietly did
/// something else". Everything here is therefore either recognized and allowed, recognized and
/// refused, or unrecognized and refused.
/// </para>
/// <para>
/// <b>Modes.</b> Only <c>100644</c>, a regular non-executable file, is accepted on a create or a
/// delete. <c>100755</c> would make a file executable, and <c>120000</c> declares a symbolic link
/// whose content is its target, which is precisely the escape the tree walk refuses to copy; neither
/// is something a content-only applier can honour, so both are refused rather than written as
/// ordinary files with the mode silently dropped. A <c>old mode</c>/<c>new mode</c> pair is a mode
/// change with no content change, which this cannot represent at all.
/// </para>
/// </remarks>
internal static class SourcePatchExtendedHeaderReader
{
    private const string RegularFileMode = "100644";

    /// <summary>Headers that describe an operation this refuses to perform.</summary>
    private static readonly string[] UnsupportedPrefixes =
    [
        "old mode ", "new mode ", "similarity index ", "dissimilarity index ",
        "rename from ", "rename to ", "copy from ", "copy to ",
    ];

    /// <summary>Headers that mark content this cannot read as text.</summary>
    private static readonly string[] BinaryPrefixes = ["GIT binary patch", "Binary files ", "Binary file "];

    public static SourcePatchExtendedHeaderScan Scan(IReadOnlyList<string> lines, int index)
    {
        var creates = false;
        var deletes = false;
        var position = index;
        while (position < lines.Count)
        {
            var line = SourcePatchText.TrimStructural(lines[position]);
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                return new SourcePatchExtendedHeaderScan(creates, deletes, null, position);
            }

            var refusal = Classify(line, ref creates, ref deletes);
            if (refusal is not null)
            {
                return SourcePatchExtendedHeaderScan.Refused(refusal);
            }

            position++;
        }

        // The section ran out before the file headers arrived, so nothing says which file it meant.
        return SourcePatchExtendedHeaderScan.Refused(SourcePatchCodes.Malformed);
    }

    private static string? Classify(string line, ref bool creates, ref bool deletes)
    {
        if (line.StartsWith("index ", StringComparison.Ordinal))
        {
            // Blob identities of a repository this process cannot consult. Read past, never trusted.
            return null;
        }

        if (line.StartsWith("new file mode ", StringComparison.Ordinal))
        {
            creates = true;
            return AcceptMode(line["new file mode ".Length..]);
        }

        if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
        {
            deletes = true;
            return AcceptMode(line["deleted file mode ".Length..]);
        }

        if (BinaryPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return SourcePatchCodes.Binary;
        }

        return UnsupportedPrefixes.Any(prefix => line.StartsWith(prefix, StringComparison.Ordinal))
            ? SourcePatchCodes.UnsupportedOperation
            : SourcePatchCodes.Malformed;
    }

    private static string? AcceptMode(string mode) =>
        string.Equals(mode, RegularFileMode, StringComparison.Ordinal)
            ? null
            : SourcePatchCodes.UnsupportedOperation;
}
