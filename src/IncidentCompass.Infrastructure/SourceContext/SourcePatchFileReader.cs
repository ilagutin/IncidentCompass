namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Reads one file section: an optional <c>diff --git</c> line, the extended headers, the
/// <c>---</c>/<c>+++</c> pair, and the hunks, checking everything that can be checked without a
/// filesystem.
/// </summary>
/// <remarks>
/// <para>
/// <b>Prefixes are required, not inferred.</b> The old side must read <c>a/&lt;path&gt;</c> or
/// <c>/dev/null</c> and the new side <c>b/&lt;path&gt;</c> or <c>/dev/null</c>. A prefix-less diff
/// is refused rather than guessed at, and a quoted path, the form git uses when a name needs
/// escaping, never starts with <c>a/</c> and so is refused too. The two sides must name the same
/// path: a section that names two paths is a rename or a copy, and both are refused by name.
/// </para>
/// <para>
/// <b>Headers must agree with each other.</b> A <c>new file mode</c> header without
/// <c>--- /dev/null</c>, or a <c>deleted file mode</c> header without <c>+++ /dev/null</c>, is a
/// section whose two halves describe different operations, and a <c>diff --git</c> line naming a
/// path the file headers do not is the same disagreement one line earlier. Both are refused instead
/// of resolved in favour of one half.
/// </para>
/// <para>
/// <b>Hunks are checked against each other before any file is opened.</b> They must run forwards
/// and must not cover the same base line twice, and each header's result start must equal its base
/// start shifted by everything the earlier hunks added or removed. That last check is what makes a
/// header's numbers mean something: without it the result side is decoration, and a patch could
/// claim any line numbering it liked.
/// </para>
/// </remarks>
internal static class SourcePatchFileReader
{
    private const string DiffPrefix = "diff --git ";
    private const string DevNull = "/dev/null";
    private const string HunkPrefix = "@@ -";

    public static SourcePatchFileRead Read(IReadOnlyList<string> lines, int index, SourcePatchLimits limits)
    {
        var position = index;
        string? declaredPath = null;
        if (SourcePatchText.TrimStructural(lines[position]).StartsWith(DiffPrefix, StringComparison.Ordinal))
        {
            declaredPath = ReadDeclaredPath(SourcePatchText.TrimStructural(lines[position]), out var declarationRefusal);
            if (declarationRefusal is not null)
            {
                return SourcePatchFileRead.Refused(declarationRefusal);
            }

            position++;
        }

        var headers = SourcePatchExtendedHeaderReader.Scan(lines, position);
        if (headers.Code is not null)
        {
            return SourcePatchFileRead.Refused(headers.Code);
        }

        var target = ReadTarget(lines, headers.NextIndex, declaredPath, headers, out var targetRefusal);
        if (targetRefusal is not null)
        {
            return SourcePatchFileRead.Refused(targetRefusal);
        }

        var pathRefusal = SourcePatchPathPolicy.Reject(target.Path, limits);
        if (pathRefusal is not null)
        {
            return SourcePatchFileRead.Refused(pathRefusal);
        }

        return ReadHunks(lines, headers.NextIndex + 2, target, limits);
    }

    /// <summary>
    /// Parses <c>diff --git a/&lt;path&gt; b/&lt;path&gt;</c>. A path cannot contain a space, so the
    /// split on <c>" b/"</c> is the only split that can produce two admissible paths; a split in the
    /// wrong place leaves a space in one of them and the path policy refuses it.
    /// </summary>
    private static string? ReadDeclaredPath(string line, out string? code)
    {
        code = null;
        var rest = line[DiffPrefix.Length..];
        var separator = rest.IndexOf(" b/", StringComparison.Ordinal);
        if (!rest.StartsWith("a/", StringComparison.Ordinal) || separator < 0)
        {
            code = SourcePatchCodes.Malformed;
            return null;
        }

        var oldPath = rest[2..separator];
        var newPath = rest[(separator + 3)..];
        if (!string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            code = SourcePatchCodes.UnsupportedOperation;
            return null;
        }

        return oldPath;
    }

    private static (string Path, SourcePatchFileKind Kind) ReadTarget(
        IReadOnlyList<string> lines,
        int index,
        string? declaredPath,
        SourcePatchExtendedHeaderScan headers,
        out string? code)
    {
        code = null;
        if (index + 1 >= lines.Count)
        {
            code = SourcePatchCodes.Malformed;
            return (string.Empty, SourcePatchFileKind.Modify);
        }

        var oldSide = ReadSide(lines[index], "--- ", "a/", out var oldCode);
        var newSide = ReadSide(lines[index + 1], "+++ ", "b/", out var newCode);
        code = oldCode ?? newCode;
        if (code is not null)
        {
            return (string.Empty, SourcePatchFileKind.Modify);
        }

        var kind = ResolveKind(oldSide, newSide, out code);
        if (code is not null)
        {
            return (string.Empty, SourcePatchFileKind.Modify);
        }

        var path = kind == SourcePatchFileKind.Create ? newSide! : oldSide!;
        if (headers.CreatesFile && kind != SourcePatchFileKind.Create)
        {
            code = SourcePatchCodes.Malformed;
        }
        else if (headers.DeletesFile && kind != SourcePatchFileKind.Delete)
        {
            code = SourcePatchCodes.Malformed;
        }
        else if (declaredPath is not null && !string.Equals(declaredPath, path, StringComparison.Ordinal))
        {
            code = SourcePatchCodes.Malformed;
        }

        return (path, kind);
    }

    /// <summary>Returns the path a file-header line names, or <c>null</c> when it names nothing.</summary>
    private static string? ReadSide(string line, string marker, string prefix, out string? code)
    {
        code = null;
        var trimmed = SourcePatchText.TrimStructural(line);
        if (!trimmed.StartsWith(marker, StringComparison.Ordinal))
        {
            code = SourcePatchCodes.Malformed;
            return null;
        }

        var value = trimmed[marker.Length..];
        if (string.Equals(value, DevNull, StringComparison.Ordinal))
        {
            return null;
        }

        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            code = SourcePatchCodes.Malformed;
            return null;
        }

        return value[prefix.Length..];
    }

    private static SourcePatchFileKind ResolveKind(string? oldSide, string? newSide, out string? code)
    {
        code = null;
        if (oldSide is null && newSide is null)
        {
            code = SourcePatchCodes.Malformed;
            return SourcePatchFileKind.Modify;
        }

        if (oldSide is null)
        {
            return SourcePatchFileKind.Create;
        }

        if (newSide is null)
        {
            return SourcePatchFileKind.Delete;
        }

        if (!string.Equals(oldSide, newSide, StringComparison.Ordinal))
        {
            code = SourcePatchCodes.UnsupportedOperation;
        }

        return SourcePatchFileKind.Modify;
    }

    private static SourcePatchFileRead ReadHunks(
        IReadOnlyList<string> lines,
        int index,
        (string Path, SourcePatchFileKind Kind) target,
        SourcePatchLimits limits)
    {
        var hunks = new List<SourcePatchHunk>();
        var position = index;

        // Widened, so that the running position and shift stay arithmetic rather than becoming a
        // wrap. The header bound already keeps both inside `int` for any configured target size,
        // but a check that only holds because of an earlier check is not a check.
        var consumedThrough = 0L;
        var delta = 0L;
        while (position < lines.Count && lines[position].StartsWith(HunkPrefix, StringComparison.Ordinal))
        {
            if (hunks.Count == limits.MaximumHunksPerFile)
            {
                return SourcePatchFileRead.Refused(SourcePatchCodes.HunkLimit);
            }

            var read = SourcePatchHunkReader.Read(lines, position, limits);
            if (read.Hunk is null)
            {
                return SourcePatchFileRead.Refused(read.Code ?? SourcePatchCodes.Malformed);
            }

            if (read.Hunk.OldIndex < consumedThrough)
            {
                return SourcePatchFileRead.Refused(SourcePatchCodes.HunkOverlap);
            }

            if (read.Hunk.NewIndex != read.Hunk.OldIndex + delta)
            {
                return SourcePatchFileRead.Refused(SourcePatchCodes.Malformed);
            }

            consumedThrough = (long)read.Hunk.OldIndex + read.Hunk.OldCount;
            delta += (long)read.Hunk.NewCount - read.Hunk.OldCount;
            hunks.Add(read.Hunk);
            position = read.NextIndex;
        }

        if (hunks.Count == 0)
        {
            return SourcePatchFileRead.Refused(SourcePatchCodes.NoHunks);
        }

        var shapeRefusal = RejectShape(target.Kind, hunks);
        return shapeRefusal is not null
            ? SourcePatchFileRead.Refused(shapeRefusal)
            : SourcePatchFileRead.Read(new SourcePatchFile(target.Path, target.Kind, hunks), position);
    }

    /// <summary>
    /// A create and a delete each have exactly one shape, and anything else claiming to be one is
    /// refused. A create must quote nothing from a base it says does not exist; a delete must quote
    /// all of the base and produce nothing, which is what makes the applier's context check cover
    /// the entire file rather than a window of it.
    /// </summary>
    private static string? RejectShape(SourcePatchFileKind kind, List<SourcePatchHunk> hunks)
    {
        if (kind == SourcePatchFileKind.Modify)
        {
            return null;
        }

        if (hunks.Count != 1)
        {
            return SourcePatchCodes.Malformed;
        }

        var hunk = hunks[0];
        return kind switch
        {
            SourcePatchFileKind.Create =>
                hunk.OldCount == 0 && hunk.OldStart == 0 && hunk.Lines.All(line => line.Origin == '+')
                    ? null
                    : SourcePatchCodes.Malformed,
            _ => hunk.NewCount == 0 && hunk.OldStart == 1 && hunk.Lines.All(line => line.Origin == '-')
                ? null
                : SourcePatchCodes.Malformed,
        };
    }
}
