using static IncidentCompass.Infrastructure.SourceContext.SourcePathBoundary;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Materializes a disposable copy of the monitored source tree and computes its tree identity.
/// </summary>
/// <remarks>
/// <para>
/// The monitored checkout is opened for reading and nothing else. Every write goes to one fresh
/// directory below the configured workspace root, and that directory is removed on every terminal
/// path: refusal, filesystem error, cancellation, and normal completion once the caller disposes the
/// workspace. Nothing here executes anything: no process is started, no file in the copy is opened
/// after it is written, and the copy is inert bytes on disk until a later caller reads it.
/// </para>
/// <para>
/// Cancellation is observed inside the walk rather than before the workspace exists, so the
/// cancellation path always runs the same deletion the failure path runs, and a test can prove it
/// with an already-cancelled token instead of a race.
/// </para>
/// </remarks>
internal sealed class SourceWorkspaceMaterializer(string workspaceRoot, SourceWorkspaceBounds bounds)
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public async Task<SourceWorkspaceResult> MaterializeAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            return SourceWorkspaceResult.Refused(SourceWorkspaceCodes.RootUnavailable);
        }

        string? workspacePath = null;
        try
        {
            // Canonicalizing the two configured roots sits inside the guard rather than above it.
            // Both strings are host configuration, and a malformed one is refused here as an
            // unavailable workspace instead of leaving an ArgumentException for a caller to catch:
            // a caller wide enough to catch it is also wide enough to catch the diff engine's own
            // defects, which must not be turned into a filesystem story.
            var canonicalRoot = ResolveRoot(Path.GetFullPath(sourceRoot));
            var resolvedWorkspaceRoot = Path.GetFullPath(workspaceRoot);
            if (IsSameOrBelow(resolvedWorkspaceRoot, canonicalRoot))
            {
                // A workspace below the monitored root would copy itself, and the copy would then be
                // part of the tree its own identity describes.
                return SourceWorkspaceResult.Refused(SourceWorkspaceCodes.WorkspaceRootRejected);
            }

            Directory.CreateDirectory(resolvedWorkspaceRoot);
            workspacePath = SourceWorkspaceDirectory.Create(resolvedWorkspaceRoot);
            return await CopyAsync(canonicalRoot, workspacePath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            SourceWorkspaceDirectory.TryDelete(workspacePath);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            SourceWorkspaceDirectory.TryDelete(workspacePath);
            return SourceWorkspaceResult.Refused(SourceWorkspaceCodes.Unavailable);
        }
    }

    private async Task<SourceWorkspaceResult> CopyAsync(
        string canonicalRoot,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        var copier = new SourceWorkspaceCopier(canonicalRoot, bounds);
        var refusal = await copier.CopyAsync(workspacePath, cancellationToken);
        if (refusal is not null)
        {
            SourceWorkspaceDirectory.TryDelete(workspacePath);
            return SourceWorkspaceResult.Refused(refusal);
        }

        return SourceWorkspaceResult.Created(new SourceWorkspace(
            workspacePath,
            SourceTreeIdentity.Compute(copier.Entries),
            copier.Entries.Count,
            copier.TotalBytes));
    }

    private static bool IsSameOrBelow(string candidate, string root) =>
        string.Equals(
            candidate.TrimEnd(Path.DirectorySeparatorChar),
            root.TrimEnd(Path.DirectorySeparatorChar),
            PathComparison) ||
        IsUnderRoot(candidate, root, PathComparison);
}
