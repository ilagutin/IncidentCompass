namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Reads one hunk: its header, its body, and any no-newline marker attached to either side.
/// </summary>
/// <remarks>
/// <para>
/// The body is read under the header's own counts rather than by looking for where the next hunk
/// starts. That is the whole point of the counts, and it removes the class of attacks that work by
/// making a body line look like a header: a line is a body line because the header said there would
/// be one, not because of what it happens to begin with. When the body runs out before the counts
/// do, or a count would go negative, the hunk is refused rather than repaired.
/// </para>
/// <para>
/// An origin character is one of exactly three, plus the marker. A zero-length line is refused
/// rather than read as an empty context line: a well-formed diff writes an empty context line as a
/// single space, and guessing which of three origins a bare empty line meant is exactly the kind of
/// repair that makes a parser's accepted language larger than its author's model of it.
/// </para>
/// </remarks>
internal static class SourcePatchHunkReader
{
    public static SourcePatchHunkRead Read(IReadOnlyList<string> lines, int index, SourcePatchLimits limits)
    {
        var header = ReadHeader(SourcePatchText.TrimStructural(lines[index]), limits);
        if (header is null)
        {
            return SourcePatchHunkRead.Refused(SourcePatchCodes.Malformed);
        }

        var (oldStart, oldCount, newStart, newCount) = header.Value;
        if (oldCount == 0 && newCount == 0)
        {
            // A hunk that consumes nothing and produces nothing describes no change and would let a
            // patch pad itself with sections that mean nothing.
            return SourcePatchHunkRead.Refused(SourcePatchCodes.Malformed);
        }

        var body = new List<SourcePatchLine>();
        var state = new SourcePatchHunkBodyState(oldCount, newCount);
        var position = index + 1;
        while (state.HasRemaining)
        {
            if (position >= lines.Count)
            {
                return SourcePatchHunkRead.Refused(SourcePatchCodes.HunkCountMismatch);
            }

            var refusal = Consume(lines[position], body, state);
            if (refusal is not null)
            {
                return SourcePatchHunkRead.Refused(refusal);
            }

            position++;
        }

        while (position < lines.Count && lines[position].Length > 0 && lines[position][0] == '\\')
        {
            var refusal = Consume(lines[position], body, state);
            if (refusal is not null)
            {
                return SourcePatchHunkRead.Refused(refusal);
            }

            position++;
        }

        return SourcePatchHunkRead.Read(
            new SourcePatchHunk(
                oldStart,
                oldCount,
                newStart,
                newCount,
                body,
                state.OldEndsWithoutNewline,
                state.NewEndsWithoutNewline),
            position);
    }

    private static string? Consume(string line, List<SourcePatchLine> body, SourcePatchHunkBodyState state)
    {
        if (line.Length == 0)
        {
            return SourcePatchCodes.Malformed;
        }

        var origin = line[0];
        if (origin == '\\')
        {
            return state.MarkNoNewline(SourcePatchText.TrimStructural(line));
        }

        if (origin is not (' ' or '-' or '+'))
        {
            return SourcePatchCodes.Malformed;
        }

        var refusal = state.Take(origin);
        if (refusal is not null)
        {
            return refusal;
        }

        body.Add(new SourcePatchLine(origin, line[1..]));
        return null;
    }

    /// <summary>
    /// Parses <c>@@ -start[,count] +start[,count] @@</c>. The trailing section heading a generator
    /// may append is read past and never used; nothing downstream can be influenced by it.
    /// </summary>
    private static (int OldStart, int OldCount, int NewStart, int NewCount)? ReadHeader(
        string line,
        SourcePatchLimits limits)
    {
        const string Opening = "@@ -";
        if (!line.StartsWith(Opening, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = line[Opening.Length..];
        var plus = rest.IndexOf(" +", StringComparison.Ordinal);
        if (plus < 0)
        {
            return null;
        }

        var afterPlus = rest[(plus + 2)..];
        var closing = afterPlus.IndexOf(" @@", StringComparison.Ordinal);
        if (closing < 0)
        {
            return null;
        }

        var oldRange = ReadRange(rest[..plus], limits);
        var newRange = ReadRange(afterPlus[..closing], limits);
        return oldRange is null || newRange is null
            ? null
            : (oldRange.Value.Start, oldRange.Value.Count, newRange.Value.Start, newRange.Value.Count);
    }

    /// <summary>
    /// Parses <c>start</c> or <c>start,count</c>. An omitted count is one, the format's own default.
    /// A range that spans lines must start at line one or later; only a range that spans nothing may
    /// start at zero, which is how the format says "before the first line".
    /// </summary>
    /// <remarks>
    /// The highest line a range names is bounded against something real: a file the patch may touch
    /// holds at most <see cref="SourcePatchLimits.MaximumTargetBytes"/> bytes, and a file of
    /// <c>n</c> bytes holds at most <c>n</c> lines, because every line costs at least its
    /// terminator. A header naming a line beyond that describes a file this would refuse to open, so
    /// it is malformed text rather than a mismatch against a base, and refusing it here keeps the
    /// refusal reproducible from the patch alone. It also keeps every later sum of a start and a
    /// count inside <see cref="int"/>: an unbounded start makes <c>start + count</c> wrap negative,
    /// and a negative offset walks off the front of a list rather than being caught by a bound
    /// written to catch walking off the end.
    /// </remarks>
    private static (int Start, int Count)? ReadRange(string value, SourcePatchLimits limits)
    {
        var comma = value.IndexOf(',');
        var startText = comma < 0 ? value.AsSpan() : value.AsSpan(0, comma);
        if (!SourcePatchText.TryParseCount(startText, out var start))
        {
            return null;
        }

        var count = 1;
        if (comma >= 0 && !SourcePatchText.TryParseCount(value.AsSpan(comma + 1), out count))
        {
            return null;
        }

        if (count > 0 && start < 1)
        {
            return null;
        }

        var highest = count == 0 ? (long)start : (long)start + count - 1;
        return highest > limits.MaximumTargetBytes ? null : (start, count);
    }
}
