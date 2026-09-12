using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.SourceContext;
using static IncidentCompass.Infrastructure.SourceContext.SourcePathBoundary;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The steps that turn an approved base and an approved diff into the exact files a push may carry,
/// plus the proof that the approved base is one remote commit.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the whole of the safety argument.</b> Identify the copy and refuse unless it is the
/// approved base; prove it against the remote listing and refuse on any divergence; only then parse
/// the diff, apply it, and re-identify. A correspondence proved after the patch would describe a tree
/// that never existed anywhere, and a patch parsed before the base check would let an insert-only hunk
/// apply cleanly to a tree nobody approved.
/// </para>
/// <para>
/// <b>Only the diff's own files are read back.</b> The rest of the copy is never opened, so nothing
/// outside the sixteen policy-checked paths a diff may name can reach a push. That is what makes the
/// untracked surplus in a working checkout - build output, local logs, an environment file admission
/// never looks at - unreachable rather than merely unwanted: it is enumerated by the correspondence,
/// excluded from the overlay, and never read.
/// </para>
/// </remarks>
internal static class RemediationPublicationPreparer
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static async Task<RemediationPublicationResult> PrepareAsync(
        SourceWorkspace workspace,
        RemediationPublicationRequest request,
        SourceContextOptions options,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(workspace.TreeIdentity, request.BaseTreeIdentity, StringComparison.Ordinal))
        {
            return RemediationPublicationResult.Refused(RemediationCodes.BaseMismatch);
        }

        var (localBlobIds, scanRefusal) = await GitBlobTreeScanner.ScanAsync(
            workspace.DirectoryPath, SourceWorkspaceBounds.Default, cancellationToken);
        if (localBlobIds is null)
        {
            return RemediationPublicationResult.Refused(scanRefusal!);
        }

        var correspondence = GitTreeCorrespondence.Compare(
            request.BaseTreeIdentity,
            request.RemoteCommitSha,
            request.RemoteTreeSha,
            localBlobIds,
            request.RemoteBlobIds);
        if (!correspondence.IsProved)
        {
            return RemediationPublicationResult.Refused(correspondence.Code);
        }

        var limits = SourcePatchLimits.For(options, SourceWorkspaceBounds.Default);
        var parsed = SourcePatchParser.Parse(request.PatchText, limits);
        if (parsed.Patch is null)
        {
            return RemediationPublicationResult.Refused(parsed.Code);
        }

        var applier = new SourcePatchApplier(workspace.DirectoryPath, limits, SourceWorkspaceBounds.Default);
        var applied = await applier.ApplyAsync(parsed.Patch, cancellationToken);
        if (applied.TreeIdentity is null)
        {
            return RemediationPublicationResult.Refused(applied.Code);
        }

        if (!string.Equals(applied.TreeIdentity, request.ResultTreeIdentity, StringComparison.Ordinal))
        {
            return RemediationPublicationResult.Refused(RemediationPublicationCodes.ResultMismatch);
        }

        var changed = await ReadChangedAsync(workspace, parsed.Patch, options, cancellationToken);
        return changed.Files is null
            ? RemediationPublicationResult.Refused(changed.Code!)
            : RemediationPublicationResult.Prepared(
                correspondence.Digest!,
                correspondence.ProvedPathCount,
                correspondence.LocalOnlyPaths,
                changed.Files);
    }

    private static async Task<(IReadOnlyList<RemediationPublicationFile>? Files, string? Code)> ReadChangedAsync(
        SourceWorkspace workspace,
        SourcePatch patch,
        SourceContextOptions options,
        CancellationToken cancellationToken)
    {
        var files = new List<RemediationPublicationFile>(patch.Files.Count);
        foreach (var file in patch.Files.OrderBy(static file => file.RepositoryPath, StringComparer.Ordinal))
        {
            if (file.Kind == SourcePatchFileKind.Delete)
            {
                files.Add(new RemediationPublicationFile(file.RepositoryPath, null));
                continue;
            }

            var absolute = Path.GetFullPath(Path.Combine(
                workspace.DirectoryPath,
                Path.Combine(file.RepositoryPath.Split('/'))));
            if (!IsUnderRoot(absolute, workspace.DirectoryPath, PathComparison) ||
                !File.Exists(absolute))
            {
                return (null, RemediationPublicationCodes.ChangedFileUnreadable);
            }

            if (new FileInfo(absolute).Length > options.MaxSourceBytes)
            {
                return (null, RemediationPublicationCodes.ChangedFileTooLarge);
            }

            files.Add(new RemediationPublicationFile(
                file.RepositoryPath,
                await File.ReadAllBytesAsync(absolute, cancellationToken)));
        }

        return (files, null);
    }
}
