namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Applies a parsed patch inside one workspace, all of it or none of it, and recomputes the tree
/// identity of what it produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>All or nothing, in two phases.</b> <see cref="SourcePatchPlanner"/> decides every file against
/// the base before anything is written, so a patch that fails on its fifth file is refused with the
/// workspace untouched. Writing is then the only step left that can fail, and it can fail for
/// reasons no inspection predicts: a full disk, a revoked permission, a path the platform will not
/// accept. Every write is therefore preceded by keeping what it replaces, and the first failure
/// undoes the commit: it deletes what was created, removes the directories the commit made, and
/// then puts back what was replaced.
/// </para>
/// <para>
/// <b>Undoing runs in that order because the reverse loses a file.</b> A commit can delete
/// <c>src/A.cs</c> and then create <c>src/A.cs/C.cs</c>, which makes <c>src/A.cs</c> a directory.
/// Putting files back first then means writing <c>src/A.cs</c> onto a directory, which fails, and
/// removing the directory afterwards leaves nothing at all where a file used to be, while the caller
/// is told the patch was refused. Creations are therefore undone before restorations, so the path a
/// restoration needs is free by the time it needs it. <see cref="SourcePatchParser"/> also refuses a
/// patch whose paths nest like that, which is the better place to refuse it, but rollback
/// correctness must not rest on a rule in the parser: this class is reachable with any
/// <see cref="SourcePatch"/>, and a check that only holds because of an earlier check is not a
/// check.
/// </para>
/// <para>
/// <b>A rollback that failed says so.</b> Restoring is best effort, because a filesystem that would
/// not accept a write may not accept the write that undoes it either. Every step therefore checks
/// what it left behind rather than assuming the exception it caught meant nothing changed: a
/// creation is undone when the path holds no file, a replacement when the file holds the bytes it
/// held. When any step cannot get there, the outcome is <see cref="SourcePatchCodes.RollbackFailed"/>
/// rather than <see cref="SourcePatchCodes.Unavailable"/>, because "nothing was applied and the
/// workspace is as it was" and "nothing was applied and the workspace is something else" are not the
/// same thing to tell a caller. On the second, the workspace must be discarded rather than read,
/// identified or reused.
/// </para>
/// <para>
/// <b>The backstop, and what it does not cover.</b> The workspace is disposable, so a caller that
/// sees a refusal it did not expect can discard the whole directory, which is the guarantee that
/// does not depend on the filesystem cooperating. Nothing here writes outside the workspace, with
/// two qualifications. The checks that establish that are made when the plan is built and the writes
/// happen afterwards, so a workspace something else is changing underneath is outside what they can
/// promise; a concurrent writer inside the workspace is outside the threat model rather than
/// impossible. And the workspace root itself is never link-resolved: everything below it is checked
/// for reparse points, but if the root a host configured is reached through one, discarding the
/// directory is still all a caller can do, and where those bytes physically live was decided by the
/// host's configuration, not here.
/// </para>
/// <para>
/// <b>What the caller must check that this does not.</b> A hunk that consumes no base line quotes no
/// base line, so it matches at its offset in any file: nothing in an insert-only patch binds it to
/// the tree it was generated against, and the context check has nothing to compare. The base tree
/// identity is the answer, and enforcing it is the caller's obligation, not this class's. A caller
/// that applies an approved patch must compare the identity of the workspace it is about to change
/// against the identity the patch was approved for, and refuse when they differ. Without that,
/// an approved diff can be applied to a tree its approver never saw.
/// </para>
/// <para>
/// <b>Nothing is executed.</b> Files are written and never opened again except to read them back for
/// the identity walk. No process is started, and the architecture guard that fails the build when
/// process I/O appears in the Application project is untouched.
/// </para>
/// </remarks>
internal sealed class SourcePatchApplier(
    string workspacePath,
    SourcePatchLimits limits,
    SourceWorkspaceBounds bounds)
{
    public async Task<SourcePatchApplyResult> ApplyAsync(SourcePatch patch, CancellationToken cancellationToken)
    {
        SourcePatchPlan plan;
        try
        {
            plan = await SourcePatchPlanner.PlanAsync(workspacePath, patch, limits, cancellationToken);
        }
        catch (Exception exception) when (IsFilesystemFailure(exception))
        {
            // Nothing was written, so there is nothing to undo.
            return SourcePatchApplyResult.Refused(SourcePatchCodes.Unavailable);
        }

        return plan.Files is null
            ? SourcePatchApplyResult.Refused(plan.Code)
            : await CommitAsync(plan.Files, cancellationToken);
    }

    private async Task<SourcePatchApplyResult> CommitAsync(
        IReadOnlyList<SourcePatchPlannedFile> files,
        CancellationToken cancellationToken)
    {
        var written = new List<SourcePatchPlannedFile>(files.Count);
        var createdDirectories = new List<string>();
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Recorded before the write, not after it. A write that fails partway has already
                // changed the file it failed on, so the file that failed is exactly the one a
                // rollback most needs to put back. Restoring a file that was never touched writes
                // the bytes it already holds, which costs a write and changes nothing.
                written.Add(file);
                await WriteAsync(file, createdDirectories, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The rollback's own outcome cannot be reported through an exception, so a cancelled
            // attempt tells the caller nothing about how far the commit got or how much of it came
            // back. A caller that cancels must discard the workspace.
            Restore(written, createdDirectories);
            throw;
        }
        catch (Exception exception) when (IsFilesystemFailure(exception))
        {
            return SourcePatchApplyResult.Refused(Undo(written, createdDirectories, SourcePatchCodes.Unavailable));
        }

        return await IdentifyAsync(written, createdDirectories, cancellationToken);
    }

    /// <summary>
    /// Recomputes the identity of the workspace after the change. A walk that refuses, because the
    /// change pushed the tree past a bound, undoes the change: an applied patch whose result has no
    /// identity would be a change nothing could name.
    /// </summary>
    private async Task<SourcePatchApplyResult> IdentifyAsync(
        List<SourcePatchPlannedFile> written,
        List<string> createdDirectories,
        CancellationToken cancellationToken)
    {
        SourceTreeScanResult scan;
        try
        {
            scan = await SourceTreeScanner.ScanAsync(workspacePath, bounds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Restore(written, createdDirectories);
            throw;
        }
        catch (Exception exception) when (IsFilesystemFailure(exception))
        {
            return SourcePatchApplyResult.Refused(Undo(written, createdDirectories, SourcePatchCodes.Unavailable));
        }

        if (scan.Entries is null)
        {
            return SourcePatchApplyResult.Refused(Undo(written, createdDirectories, scan.Code));
        }

        return SourcePatchApplyResult.Applied(SourceTreeIdentity.Compute(scan.Entries), written.Count);
    }

    /// <summary>
    /// Rolls the commit back and returns the code to report: the one that describes why the attempt
    /// ended, or <see cref="SourcePatchCodes.RollbackFailed"/> when the workspace could not be put
    /// back, since the reason the attempt ended stops being the most important thing a caller needs
    /// to know once the workspace is no longer what it was.
    /// </summary>
    private static string Undo(
        List<SourcePatchPlannedFile> written,
        List<string> createdDirectories,
        string code) =>
        Restore(written, createdDirectories) ? code : SourcePatchCodes.RollbackFailed;

    private static async Task WriteAsync(
        SourcePatchPlannedFile file,
        List<string> createdDirectories,
        CancellationToken cancellationToken)
    {
        if (file.ResultContent is null)
        {
            File.Delete(file.AbsolutePath);
            return;
        }

        if (file.Kind == SourcePatchFileKind.Create)
        {
            CreateParentDirectories(file.AbsolutePath, createdDirectories);
        }

        await File.WriteAllBytesAsync(file.AbsolutePath, file.ResultContent, cancellationToken);
    }

    /// <summary>
    /// Creates the missing parents of a new file, recording each one so a rollback can take exactly
    /// the directories this commit added and no others.
    /// </summary>
    private static void CreateParentDirectories(string absolutePath, List<string> createdDirectories)
    {
        var missing = new Stack<string>();
        var directory = Path.GetDirectoryName(absolutePath);
        while (directory is not null && !Directory.Exists(directory))
        {
            missing.Push(directory);
            directory = Path.GetDirectoryName(directory);
        }

        while (missing.Count > 0)
        {
            var path = missing.Pop();
            Directory.CreateDirectory(path);
            createdDirectories.Add(path);
        }
    }

    /// <summary>
    /// Undoes the commit in three passes and returns whether the workspace is what it was:
    /// created files, then created directories, then replaced content. Returns <c>false</c> when any
    /// step could not get there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order matters and the reverse loses data; see the type's remarks. Within each pass the
    /// work runs deepest first, so a directory is emptied before it is removed.
    /// </para>
    /// <para>
    /// Every step is tolerant of already being undone, because the failure that triggered a rollback
    /// may have left the filesystem in a state where some of the undoing is unnecessary. Tolerant is
    /// not the same as blind: a step that throws asks the filesystem what is actually there before
    /// it decides whether anything was lost, which is what keeps a delete that failed because the
    /// commit never got that far from reading as a workspace that could not be restored.
    /// </para>
    /// <para>
    /// Internal rather than private so a test can assert the outcome directly. A genuine restore
    /// failure needs the filesystem to accept a write and then refuse the write that undoes it,
    /// which no test can arrange in process, and an untested branch in a rollback is exactly the
    /// kind of code that is wrong when it finally runs.
    /// </para>
    /// </remarks>
    internal static bool Restore(
        List<SourcePatchPlannedFile> written,
        List<string> createdDirectories)
    {
        var restored = true;
        for (var index = written.Count - 1; index >= 0; index--)
        {
            if (written[index].OriginalContent is null)
            {
                restored &= RemoveCreatedFile(written[index]);
            }
        }

        for (var index = createdDirectories.Count - 1; index >= 0; index--)
        {
            restored &= RemoveCreatedDirectory(createdDirectories[index]);
        }

        for (var index = written.Count - 1; index >= 0; index--)
        {
            if (written[index].OriginalContent is not null)
            {
                restored &= PutBackOriginal(written[index]);
            }
        }

        return restored;
    }

    private static bool RemoveCreatedFile(SourcePatchPlannedFile file)
    {
        try
        {
            File.Delete(file.AbsolutePath);
            return true;
        }
        catch (Exception exception) when (IsFilesystemFailure(exception))
        {
            // A delete that failed does not mean the commit's file is still there. A write that
            // never succeeded left nothing to remove, and a path a directory occupies refuses a
            // file delete whether or not the commit ever wrote to it.
            return !File.Exists(file.AbsolutePath);
        }
    }

    private static bool RemoveCreatedDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception exception) when (IsFilesystemFailure(exception))
        {
        }

        // A directory this commit created and could not remove is a directory the workspace did not
        // have, whether the removal threw or was skipped because something is still inside it.
        return !Directory.Exists(path);
    }

    private static bool PutBackOriginal(SourcePatchPlannedFile file)
    {
        try
        {
            File.WriteAllBytes(file.AbsolutePath, file.OriginalContent!);
            return true;
        }
        catch (Exception exception) when (IsFilesystemFailure(exception))
        {
            return HoldsOriginal(file);
        }
    }

    private static bool HoldsOriginal(SourcePatchPlannedFile file)
    {
        try
        {
            return File.Exists(file.AbsolutePath) &&
                   File.ReadAllBytes(file.AbsolutePath).AsSpan().SequenceEqual(file.OriginalContent!);
        }
        catch (Exception exception) when (IsFilesystemFailure(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Whether an exception is the filesystem refusing, as opposed to this code asking wrongly.
    /// </summary>
    /// <remarks>
    /// The line is drawn at what the two kinds mean. <see cref="IOException"/> and
    /// <see cref="UnauthorizedAccessException"/> are the filesystem's answers: a full disk, a
    /// sharing violation, a revoked permission, a path a directory occupies, a name the platform
    /// will not accept. <see cref="ArgumentException"/> and <see cref="NotSupportedException"/> are
    /// answers about the arguments, which this code chose, so they are its own defects. Including
    /// them here turned every such defect into
    /// <see cref="SourcePatchCodes.Unavailable"/>, whose own documentation says a filesystem error
    /// ended the attempt: an index computed out of range and a lone surrogate in an added line both
    /// reported a disk problem that had not happened. They are deliberately not caught, so a defect
    /// surfaces as a defect. A refusable input that used to arrive as one of them is refused by name
    /// instead, before it reaches here.
    /// </remarks>
    private static bool IsFilesystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}
