namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Applies a file section's hunks to the text of the file it names, in memory, producing the text
/// the file would have. It writes nothing and reads nothing; it is a function from a base and a
/// patch to either a result or a refusal.
/// </summary>
/// <remarks>
/// <para>
/// <b>Context is checked, never searched for.</b> Every base line a hunk names is compared byte for
/// byte at exactly the offset the header put it at. There is no fuzz, no offset search and no
/// whitespace tolerance, all of which real patch tools have and all of which mean "apply this
/// somewhere near where it said". A patch that does not match where it claims to match was generated
/// against a different base, and applying it anyway is how a reviewed diff turns into an unreviewed
/// edit.
/// </para>
/// <para>
/// <b>The end of the file is part of the comparison.</b> Whether the base ends with a terminator is
/// checked against the hunk that reaches the end, and the result's terminator comes from the same
/// hunk. A patch may not append to a file that ends without one, because doing so would silently
/// insert a terminator the patch never wrote, and the tree identity would then record a change
/// nobody described.
/// </para>
/// </remarks>
internal static class SourcePatchHunkApplier
{
    /// <summary>
    /// Returns a refusal code, or <c>null</c> with <paramref name="applied"/> set to the result.
    /// </summary>
    public static string? TryApply(
        SourceTextFile file,
        IReadOnlyList<SourcePatchHunk> hunks,
        out SourceTextFile? applied)
    {
        applied = null;
        var source = file.Lines;
        var result = new List<string>();
        var cursor = 0;
        var endsWithNewline = file.EndsWithNewline;
        var endAlreadyReached = false;
        foreach (var hunk in hunks)
        {
            var start = hunk.OldIndex;

            // Summed as a `long`. The header bound already refuses a start no file this admits
            // could have, but this guard is the last thing between a header's numbers and an
            // indexed read, so it must be arithmetic that cannot wrap rather than arithmetic that
            // is kept from wrapping by a rule in another file: `start + hunk.OldCount` in `int`
            // turns a start near `int.MaxValue` into a negative sum, which is not greater than the
            // line count, and the guard then admits exactly the hunk it exists to refuse.
            if (start < cursor || (long)start + hunk.OldCount > source.Count)
            {
                return SourcePatchCodes.ContextMismatch;
            }

            for (var index = cursor; index < start; index++)
            {
                result.Add(source[index]);
            }

            cursor = start;
            foreach (var line in hunk.Lines)
            {
                if (line.Origin == '+')
                {
                    result.Add(line.Text);
                    continue;
                }

                if (!string.Equals(source[cursor], line.Text, StringComparison.Ordinal))
                {
                    return SourcePatchCodes.ContextMismatch;
                }

                if (line.Origin == ' ')
                {
                    result.Add(line.Text);
                }

                cursor++;
            }

            var reachesEnd = (long)start + hunk.OldCount == source.Count;
            var refusal = RejectEnding(file, hunk, source.Count, reachesEnd, ref endAlreadyReached);
            if (refusal is not null)
            {
                return refusal;
            }

            if (reachesEnd)
            {
                endsWithNewline = !hunk.NewEndsWithoutNewline;
            }
        }

        for (var index = cursor; index < source.Count; index++)
        {
            result.Add(source[index]);
        }

        applied = new SourceTextFile(result, endsWithNewline);
        return null;
    }

    /// <remarks>
    /// Four rules, each closing one way a patch could change the file's final byte without saying
    /// so. A hunk that consumes the last line must agree with the base about whether that line has a
    /// terminator. A hunk that only inserts at the end of a file that has no terminator is refused,
    /// because the lines before the insertion would each gain one. A no-newline marker on a hunk
    /// that stops short of the end of the file describes an end that is not there, on whichever side
    /// carries it: a marker is a claim about a file's last byte, and a hunk that does not reach the
    /// last byte is in no position to make one. And two hunks may not both claim the end, because
    /// then the file's last byte depends on which of them is read as last.
    /// </remarks>
    private static string? RejectEnding(
        SourceTextFile file,
        SourcePatchHunk hunk,
        int sourceCount,
        bool reachesEnd,
        ref bool endAlreadyReached)
    {
        if (!reachesEnd)
        {
            return hunk.OldEndsWithoutNewline || hunk.NewEndsWithoutNewline
                ? SourcePatchCodes.ContextMismatch
                : null;
        }

        if (endAlreadyReached)
        {
            return SourcePatchCodes.ContextMismatch;
        }

        endAlreadyReached = true;
        if (hunk.OldCount > 0)
        {
            return file.EndsWithNewline == hunk.OldEndsWithoutNewline
                ? SourcePatchCodes.ContextMismatch
                : null;
        }

        return sourceCount > 0 && !file.EndsWithNewline ? SourcePatchCodes.ContextMismatch : null;
    }
}
