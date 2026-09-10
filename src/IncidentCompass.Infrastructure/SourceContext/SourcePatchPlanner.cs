using static IncidentCompass.Infrastructure.SourceContext.SourcePathBoundary;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Decides the whole patch against the workspace without writing anything: what each file holds now,
/// what it would hold, and whether every one of them can be done.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why deciding is separate from doing.</b> The applier must be all or nothing, and the cheapest
/// honest way to get that is to make every decision that can fail before the first byte is written.
/// A patch whose fifth file does not match is refused here, with the workspace still untouched,
/// rather than half-applied and then undone. What remains after this is only the writing, which can
/// still fail for reasons no inspection predicts, and that is what the commit's rollback is for.
/// </para>
/// <para>
/// <b>The path is checked twice, differently.</b> The parser refused paths on the shape of their
/// text. This one combines the surviving path with the workspace root, canonicalizes the result and
/// requires it to still be below the root, then walks the segments that exist and refuses a reparse
/// point or a name that differs from the patch's only in case. The first check does not trust the
/// filesystem and the second does not trust the text.
/// </para>
/// </remarks>
internal static class SourcePatchPlanner
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static async Task<SourcePatchPlan> PlanAsync(
        string workspacePath,
        SourcePatch patch,
        SourcePatchLimits limits,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(workspacePath);
        var planned = new List<SourcePatchPlannedFile>(patch.Files.Count);
        foreach (var file in patch.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absolute = Path.GetFullPath(Path.Combine(root, NormalizeRelative(file.RepositoryPath)));
            if (!IsUnderRoot(absolute, root, PathComparison))
            {
                return SourcePatchPlan.Refused(SourcePatchCodes.PathRejected);
            }

            var segmentRefusal = RejectExistingSegments(root, file.RepositoryPath);
            if (segmentRefusal is not null)
            {
                return SourcePatchPlan.Refused(segmentRefusal);
            }

            var result = await PlanFileAsync(file, absolute, limits, cancellationToken);
            if (result.Files is null)
            {
                return result;
            }

            planned.Add(result.Files[0]);
        }

        return SourcePatchPlan.Planned(planned);
    }

    private static async Task<SourcePatchPlan> PlanFileAsync(
        SourcePatchFile file,
        string absolute,
        SourcePatchLimits limits,
        CancellationToken cancellationToken)
    {
        if (file.Kind == SourcePatchFileKind.Create)
        {
            return PlanCreate(file, absolute, limits);
        }

        if (!File.Exists(absolute))
        {
            // A modify or a delete asserts the file is in the base. When it is not, the patch was
            // generated against a base this workspace is not, and the safe reading of "the file I
            // was told to change is missing" is never "then there is nothing to do".
            return SourcePatchPlan.Refused(SourcePatchCodes.TargetMissing);
        }

        if (new FileInfo(absolute).Length > limits.MaximumTargetBytes)
        {
            return SourcePatchPlan.Refused(SourcePatchCodes.TargetTooLarge);
        }

        var original = await File.ReadAllBytesAsync(absolute, cancellationToken);
        var text = SourceTextFile.TryDecode(original);
        if (text is null)
        {
            return SourcePatchPlan.Refused(SourcePatchCodes.TargetBinary);
        }

        var refusal = SourcePatchHunkApplier.TryApply(text, file.Hunks, out var applied);
        if (refusal is not null || applied is null)
        {
            return SourcePatchPlan.Refused(refusal ?? SourcePatchCodes.ContextMismatch);
        }

        return file.Kind == SourcePatchFileKind.Delete
            ? PlanDelete(file, absolute, original, applied)
            : PlanModify(file, absolute, original, applied, limits);
    }

    /// <summary>
    /// A create asserts the file is absent. When it is present the patch was generated against a
    /// different base, and writing anyway would replace a file whose content no hunk ever quoted,
    /// which is the one way a change can reach a file without passing the context check at all.
    /// </summary>
    private static SourcePatchPlan PlanCreate(SourcePatchFile file, string absolute, SourcePatchLimits limits)
    {
        if (File.Exists(absolute))
        {
            return SourcePatchPlan.Refused(SourcePatchCodes.TargetExists);
        }

        var hunk = file.Hunks[0];
        var content = SourceTextFile.TryRender(
            [.. hunk.Lines.Select(line => line.Text)],
            !hunk.NewEndsWithoutNewline);
        if (content is null)
        {
            return SourcePatchPlan.Refused(SourcePatchCodes.Unencodable);
        }

        return content.Length > limits.MaximumTargetBytes
            ? SourcePatchPlan.Refused(SourcePatchCodes.TargetTooLarge)
            : SourcePatchPlan.Planned([new SourcePatchPlannedFile(
                file.RepositoryPath,
                absolute,
                file.Kind,
                OriginalContent: null,
                content)]);
    }

    /// <summary>
    /// A delete must have quoted the whole file, so applying its hunk must leave nothing. Anything
    /// left means the patch described part of a file and asked for all of it to go.
    /// </summary>
    private static SourcePatchPlan PlanDelete(
        SourcePatchFile file,
        string absolute,
        byte[] original,
        SourceTextFile applied) =>
        applied.Lines.Count > 0
            ? SourcePatchPlan.Refused(SourcePatchCodes.ContextMismatch)
            : SourcePatchPlan.Planned([new SourcePatchPlannedFile(
                file.RepositoryPath,
                absolute,
                file.Kind,
                original,
                ResultContent: null)]);

    private static SourcePatchPlan PlanModify(
        SourcePatchFile file,
        string absolute,
        byte[] original,
        SourceTextFile applied,
        SourcePatchLimits limits)
    {
        var content = SourceTextFile.TryRender(applied.Lines, applied.EndsWithNewline);
        if (content is null)
        {
            return SourcePatchPlan.Refused(SourcePatchCodes.Unencodable);
        }

        return content.Length > limits.MaximumTargetBytes
            ? SourcePatchPlan.Refused(SourcePatchCodes.TargetTooLarge)
            : SourcePatchPlan.Planned([new SourcePatchPlannedFile(
                file.RepositoryPath,
                absolute,
                file.Kind,
                original,
                content)]);
    }

    /// <summary>
    /// Walks the segments of the path that already exist, stopping where the path stops existing,
    /// and refuses a reparse point or a segment the workspace spells differently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reparse points.</b> The shared boundary helper needs every segment to exist, which a
    /// create's path does not, so the walk is written out here. The workspace was copied by a walk
    /// that refuses reparse points outright, so this should never fire; it fires if something
    /// changed the workspace between the copy and now, and a check that only holds because of an
    /// earlier check is not a check.
    /// </para>
    /// <para>
    /// <b>Case.</b> <see cref="File.Exists(string)"/> answers a different question on each platform,
    /// so it cannot be the whole of a lookup that has to mean one thing. With <c>src/a.cs</c> in the
    /// workspace, a section naming <c>src/A.cs</c> modifies the existing file on Windows and is
    /// refused as missing on Linux, and a section creating it is refused as existing on Windows and
    /// produces a second file on Linux. On Windows it was also worse than divergent: the plan
    /// recorded the path the patch wrote while the identity walk afterwards reported the name the
    /// disk held, so a record of the change named a file the tree does not contain. The parser
    /// already refuses two sections that are one path on Windows and two on Linux; this is the same
    /// commitment against the workspace instead of against the patch. So the comparison is made
    /// explicitly, in the same direction on both platforms: an entry that matches the segment
    /// exactly is the segment, an entry that matches it only ignoring case is a refusal wherever the
    /// worker runs, and no entry at all means the path stops here, which is what a create's last
    /// segment is expected to do.
    /// </para>
    /// </remarks>
    private static string? RejectExistingSegments(string root, string repositoryPath)
    {
        var current = root;
        foreach (var segment in repositoryPath.Split('/'))
        {
            var onDisk = FindOnDiskName(current, segment);
            if (onDisk is null)
            {
                return null;
            }

            if (!string.Equals(onDisk, segment, StringComparison.Ordinal))
            {
                return SourcePatchCodes.PathCaseMismatch;
            }

            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return SourcePatchCodes.LinkRejected;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns <paramref name="segment"/> when the directory holds an entry of exactly that name,
    /// the differently-cased name when it holds one that matches only ignoring case, or <c>null</c>
    /// when it holds neither.
    /// </summary>
    /// <remarks>
    /// The directory is enumerated rather than searched by pattern, because a segment is
    /// attacker-influenced text and a search pattern would read <c>?</c> and <c>*</c> in it as a
    /// glob. An exact match wins over a case-insensitive one, so a tree that genuinely holds both
    /// spellings, which only a case-sensitive filesystem can, resolves to the one the patch named
    /// rather than to whichever the enumeration happened to reach first. The cost is one directory
    /// listing per existing segment per file section, bounded by the file and depth limits, which is
    /// a plan-time cost paid once and not worth trading a correct comparison for.
    /// </remarks>
    private static string? FindOnDiskName(string directory, string segment)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string? caseInsensitive = null;
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (string.Equals(entry.Name, segment, StringComparison.Ordinal))
            {
                return segment;
            }

            if (string.Equals(entry.Name, segment, StringComparison.OrdinalIgnoreCase))
            {
                caseInsensitive = entry.Name;
            }
        }

        return caseInsensitive;
    }
}
