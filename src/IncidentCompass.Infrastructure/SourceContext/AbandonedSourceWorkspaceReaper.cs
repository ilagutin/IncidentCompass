using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// One bounded run of abandoned remediation-workspace reaping: deletes prefixed directories under
/// the configured workspace root that no running pass can still own. It is a plain callable
/// operation with no schedule of its own, so a host, a test or an operator-facing entry point can
/// each drive it the same way, exactly as the two row retention operations are driven.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why anything is left behind at all.</b> <see cref="SourceWorkspaceMaterializer"/> deletes its
/// directory on every terminal path, and <see cref="SourceWorkspace.Dispose"/> deletes it again on
/// the success path. Neither covers the process being killed between the copy and the delete, and
/// neither covers a delete the filesystem refused. What is left then is disk under a directory the
/// operator declared, which is why that root is configured rather than implicit and why this run
/// exists.
/// </para>
/// <para>
/// <b>It does not try to detect liveness, because it cannot.</b> There is no in-band signal that
/// says a directory is in use: nothing writes to a workspace while the model is being asked for a
/// diff, so a modification time says only when the copy finished, and a directory nothing is writing
/// to looks exactly like a directory nobody owns. A lock file would change the tree the identity is
/// computed over, and a heartbeat would make correctness depend on which of two timers fires first.
/// So this run relies on an invariant instead of a signal: a workspace never outlives one call to
/// <c>IRemediationWorkspace</c>, because the adapter materializes a copy and disposes it before
/// returning. The longest a live workspace can exist is therefore one bounded tree copy plus one
/// bounded patch apply, and the retention window is orders of magnitude above that. This is the safe
/// design rather than the clever one: a leftover that survives an extra pass costs disk, and a
/// workspace deleted out from under a running pass costs a refusal that looks like a filesystem
/// fault.
/// </para>
/// <para>
/// <b>Two ages, and both must be old.</b> A directory is reapable only when the instant its own name
/// states is older than the window <em>and</em> its last write is older than the window. The name is
/// what "age" actually means here and is written once by the process that created it; the write time
/// is the second opinion that keeps a directory something is still filling from being deleted while
/// it is filled. Either one looking recent keeps the directory, so every ambiguity resolves toward
/// keeping. A directory whose name this code cannot read is never deleted: an unreadable name is not
/// an old workspace, it is something this run has no business dating.
/// </para>
/// <para>
/// <b>Bounded, idempotent and safe to interrupt.</b> A run deletes at most the configured number of
/// directories and stops enumerating once that budget is spent. Deleting a directory that another
/// host already deleted is not an error. A recursive delete cut short by a kill leaves a partial
/// directory that still carries the prefix and still carries an old name, so the next run finishes
/// the job. Cancellation is rethrown rather than counted as a failure, so a shutdown ends the run
/// instead of being retried as if it were one.
/// </para>
/// </remarks>
public sealed partial class AbandonedSourceWorkspaceReaper(
    IOptions<SourceContextOptions> sourceContextOptions,
    IOptions<SourceWorkspaceRetentionOptions> retentionOptions,
    TimeProvider timeProvider,
    ILogger<AbandonedSourceWorkspaceReaper> logger)
{
    public Task<SourceWorkspaceReapOutcome> ReapAsync(CancellationToken cancellationToken)
    {
        var workspaceRoot = sourceContextOptions.Value.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            return Task.FromResult(Report(SourceWorkspaceReapOutcome.Refused(
                SourceWorkspaceReapCodes.NotConfigured)));
        }

        if (!Directory.Exists(workspaceRoot))
        {
            return Task.FromResult(Report(SourceWorkspaceReapOutcome.Refused(
                SourceWorkspaceReapCodes.RootAbsent)));
        }

        var settings = retentionOptions.Value;
        var cutoffUtc = timeProvider.GetUtcNow().AddHours(-settings.RetentionHours);
        return Task.FromResult(Report(
            Sweep(workspaceRoot, cutoffUtc, settings.MaxDirectoriesPerRun, cancellationToken)));
    }

    private static SourceWorkspaceReapOutcome Sweep(
        string workspaceRoot,
        DateTimeOffset cutoffUtc,
        int maxDirectories,
        CancellationToken cancellationToken)
    {
        var examined = 0;
        var reaped = 0;
        var kept = 0;
        var failed = 0;
        try
        {
            foreach (var path in Directory.EnumerateDirectories(
                workspaceRoot, SourceWorkspaceDirectory.NamePrefix + "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reaped + failed >= maxDirectories)
                {
                    break;
                }

                examined++;
                if (!IsReapable(path, cutoffUtc))
                {
                    kept++;
                    continue;
                }

                SourceWorkspaceDirectory.TryDelete(path);
                if (Directory.Exists(path))
                {
                    failed++;
                }
                else
                {
                    reaped++;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SourceWorkspaceReapOutcome(
                SourceWorkspaceReapCodes.Unavailable, examined, reaped, kept, failed);
        }

        return new SourceWorkspaceReapOutcome(
            SourceWorkspaceReapCodes.Completed, examined, reaped, kept, failed);
    }

    /// <summary>
    /// Whether no running pass can still own this directory. Both ages have to be past the cutoff,
    /// and a name that cannot be dated is never reapable.
    /// </summary>
    /// <remarks>
    /// The write time is read defensively: a directory that vanished between the listing and this
    /// check reports a sentinel time far in the past, which would read as "old". Treating a missing
    /// directory as not reapable keeps the decision honest, and the deletion of a directory that is
    /// already gone would have been a no-op anyway.
    /// </remarks>
    private static bool IsReapable(string path, DateTimeOffset cutoffUtc)
    {
        if (!SourceWorkspaceDirectory.TryReadCreatedAtUtc(
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                out var createdAtUtc) ||
            createdAtUtc > cutoffUtc)
        {
            return false;
        }

        try
        {
            return Directory.Exists(path) &&
                new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero) <= cutoffUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private SourceWorkspaceReapOutcome Report(SourceWorkspaceReapOutcome outcome)
    {
        LogReaped(logger, outcome.Code, outcome.Examined, outcome.Reaped, outcome.Kept, outcome.Failed);
        return outcome;
    }

    [LoggerMessage(
        EventId = 3703,
        Level = LogLevel.Information,
        Message = "Abandoned remediation workspace reaping ended as {Outcome} after examining {Examined} workspace(s): {Reaped} deleted, {Kept} left alone, {Failed} could not be deleted.")]
    private static partial void LogReaped(
        ILogger logger,
        string outcome,
        int examined,
        int reaped,
        int kept,
        int failed);
}
