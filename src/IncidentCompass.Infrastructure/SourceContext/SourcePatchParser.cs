using System.Text;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Parses a unified diff into a <see cref="SourcePatch"/>, or refuses it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The input is hostile.</b> A patch is model text, and the model that wrote it was shown
/// incident data an attacker may control, so this treats the text as an untrusted document rather
/// than as output from a tool that meant well. Nothing here is repaired, inferred or given the
/// benefit of the doubt: the accepted language is a small subset of the unified-diff format, and
/// everything outside it is refused with a code. The parser is total in the sense that matters, that
/// every input either produces a patch that passed every structural check or produces a refusal, and
/// no input produces a patch that was partly checked.
/// </para>
/// <para>
/// <b>What is refused here rather than later.</b> Everything that is a property of the text: its
/// size, its shape, its paths, its hunk arithmetic and its use of operations this does not
/// implement. Nothing here opens a file, so nothing here can be affected by what the workspace
/// happens to contain, and a refusal at this stage is reproducible from the patch alone. What is
/// left for <see cref="SourcePatchApplier"/> is exactly what needs the base: whether a target
/// exists, whether it is readable text, and whether the context matches.
/// </para>
/// <para>
/// <b>Line endings.</b> The text is cut on line feeds and a body line keeps its carriage return, so
/// a patch written against one line ending does not silently apply to the other. See
/// <see cref="SourcePatchText"/>.
/// </para>
/// <para>
/// <b>What a body line may hold, and why the answer is different from a path's.</b> A path is
/// refused down to printable ASCII, because a reviewer must read the same path a filesystem opens.
/// A body line is not filtered at all: it may carry a bidirectional override, a zero-width space, a
/// byte-order mark or anything else the file it quotes may carry. That is deliberate and it is not
/// an oversight. A body line's whole job is to be the file's exact bytes, which is what the context
/// check compares and what the tree identity is computed over; filtering it would make an ordinary
/// file with a byte-order mark unpatchable and would break the round trip
/// <see cref="SourceTextFile"/> depends on. The two cases also differ in what goes wrong. A
/// hostile path makes a reviewer approve a change to a file they did not see, which nothing
/// downstream can recover from, so it is refused here. A hostile body line makes a reviewer misread
/// code whose bytes are exactly the bytes that land, which is a rendering problem: the boundary that
/// shows a diff to a human must render format and bidirectional characters visibly, and that
/// obligation belongs to the renderer, which does not exist yet, rather than to a parser that must
/// stay byte-exact. Refusing them here would also be theatre, since a patch can hide meaning in ways
/// no parser can judge.
/// </para>
/// </remarks>
internal static class SourcePatchParser
{
    public static SourcePatchParseResult Parse(string patch, SourcePatchLimits limits)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return SourcePatchParseResult.Refused(SourcePatchCodes.Empty);
        }

        if (Encoding.UTF8.GetByteCount(patch) > limits.MaximumPatchBytes)
        {
            // Measured before anything is parsed, so an oversized patch costs a byte count and
            // nothing else.
            return SourcePatchParseResult.Refused(SourcePatchCodes.TooLarge);
        }

        if (patch.Contains('\0'))
        {
            return SourcePatchParseResult.Refused(SourcePatchCodes.Malformed);
        }

        if (HasUnpairedSurrogate(patch))
        {
            return SourcePatchParseResult.Refused(SourcePatchCodes.Unencodable);
        }

        var lines = SourcePatchText.SplitLines(patch);
        var files = new List<SourcePatchFile>();
        // Case-insensitively, on every platform. Two sections differing only in case are one file on
        // Windows and two on Linux, and a patch whose meaning depends on where the worker runs is
        // refused in both places rather than accepted in one.
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        while (position < lines.Count)
        {
            if (files.Count == limits.MaximumFiles)
            {
                return SourcePatchParseResult.Refused(SourcePatchCodes.FileLimit);
            }

            var read = SourcePatchFileReader.Read(lines, position, limits);
            if (read.File is null)
            {
                return SourcePatchParseResult.Refused(read.Code ?? SourcePatchCodes.Malformed);
            }

            if (!paths.Add(read.File.RepositoryPath))
            {
                // Two sections for one path leave the second section's line numbers ambiguous: they
                // could address the base or the file the first section already produced, and the
                // format does not say which. A patch that has to be disambiguated by a reader's
                // assumption is not a patch a reviewer can approve.
                return SourcePatchParseResult.Refused(SourcePatchCodes.DuplicatePath);
            }

            if (ConflictsWithAnEarlierPath(files, read.File.RepositoryPath))
            {
                return SourcePatchParseResult.Refused(SourcePatchCodes.PathConflict);
            }

            files.Add(read.File);
            position = read.NextIndex;
        }

        return files.Count == 0
            ? SourcePatchParseResult.Refused(SourcePatchCodes.Empty)
            : SourcePatchParseResult.Parsed(new SourcePatch(files));
    }

    /// <summary>
    /// Whether one section's path is a directory prefix of another's, in either direction.
    /// </summary>
    /// <remarks>
    /// Such a patch asks for one name to be a file and a directory at once: deleting
    /// <c>src/A.cs</c> and creating <c>src/A.cs/C.cs</c> is only ever coherent in one order, and the
    /// unified-diff format says nothing about the order sections are applied in. It is refused for
    /// the reason two sections for one path are refused, one step further out: a patch a reader has
    /// to disambiguate by assumption is not a patch a reviewer can approve, and here the assumption
    /// decides whether an approved diff destroys a file the reviewer never saw named as deleted.
    /// Compared ignoring case, like the duplicate rule, so a patch that means one thing on Windows
    /// and another on Linux is refused in both places rather than accepted in one. The applier's
    /// rollback does not depend on this holding; see <see cref="SourcePatchApplier"/>.
    /// </remarks>
    private static bool ConflictsWithAnEarlierPath(List<SourcePatchFile> files, string path)
    {
        foreach (var earlier in files)
        {
            if (IsDirectoryPrefix(earlier.RepositoryPath, path) || IsDirectoryPrefix(path, earlier.RepositoryPath))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDirectoryPrefix(string prefix, string path) =>
        path.Length > prefix.Length &&
        path[prefix.Length] == '/' &&
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the text holds a surrogate with no partner, which denotes no character and so is not
    /// text any file could hold.
    /// </summary>
    /// <remarks>
    /// Refused here, over the whole patch, rather than where an added line is encoded. A lone
    /// surrogate survives JSON transport as <c>\uD800</c> and reaches this as an ordinary
    /// <see cref="char"/>; encoding it throws, and the exception it throws derives from
    /// <see cref="ArgumentException"/>, which is how a refusable input used to arrive at the applier
    /// disguised as a filesystem failure. It is a property of the text, so it belongs with the other
    /// properties of the text, and the refusal is reproducible from the patch alone.
    /// </remarks>
    private static bool HasUnpairedSurrogate(string patch)
    {
        for (var index = 0; index < patch.Length; index++)
        {
            if (!char.IsSurrogate(patch[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(patch[index]) ||
                index + 1 >= patch.Length ||
                !char.IsLowSurrogate(patch[index + 1]))
            {
                return true;
            }

            index++;
        }

        return false;
    }
}
