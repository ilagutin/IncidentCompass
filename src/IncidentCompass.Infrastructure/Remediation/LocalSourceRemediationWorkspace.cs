using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.SourceContext;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The local-filesystem adapter behind <see cref="IRemediationWorkspace" />: it copies the monitored
/// checkout into a disposable workspace, identifies it, and applies one candidate diff there.
/// </summary>
/// <remarks>
/// <para>
/// <b>It composes the source primitives rather than reimplementing them.</b> Materialization,
/// admission rules, tree identity, patch parsing and patch application all live in
/// <c>Infrastructure/SourceContext</c> and are used here exactly as they are, so the bounds a
/// remediation pass runs under are the bounds the source-read boundary already runs under and the
/// identity it computes is comparable with the one that boundary would compute. Both the size bound
/// and the extension set come from <see cref="SourceContextOptions" /> through
/// <see cref="SourcePatchLimits.For" />, so lowering either in configuration also lowers what a
/// remediation diff may leave behind.
/// </para>
/// <para>
/// <b>A workspace never outlives a call.</b> Each call materializes its own copy and disposes it
/// before returning, so no handle, lease or lifetime crosses the port and there is nothing for a
/// caller to forget to release. That costs one tree copy per call, which is the price of not holding
/// a directory open across a model call, and it is what makes the base check real rather than
/// nominal: the copy an apply checks is a copy taken now, so a checkout that moved between naming
/// the base and applying the diff is caught here rather than assumed away.
/// </para>
/// <para>
/// <b>The patched tree is deliberately thrown away.</b> What a pass produces is the diff and the two
/// identities, and the diff can be applied again against the same base whenever it is approved.
/// Keeping the tree would mean keeping a directory alive across an approval that may take days, for
/// a result that is reproducible from bytes that are already durable.
/// </para>
/// <para>
/// <b>Nothing here starts a process.</b> Files are written and read; none is executed.
/// </para>
/// </remarks>
internal sealed class LocalSourceRemediationWorkspace(IOptions<SourceContextOptions> optionsAccessor)
    : IRemediationWorkspace
{
    private readonly SourceContextOptions options = optionsAccessor.Value;

    public async Task<RemediationBaseResult> IdentifyBaseAsync(
        RemediationTarget target,
        CancellationToken cancellationToken)
    {
        if (Resolve(target) is not { } selection)
        {
            return RemediationBaseResult.Refused(RemediationCodes.NotConfigured);
        }

        try
        {
            var materialized = await MaterializeAsync(selection, cancellationToken);
            using var workspace = materialized.Workspace;
            return workspace is null
                ? RemediationBaseResult.Refused(materialized.Code)
                : RemediationBaseResult.Identified(workspace.TreeIdentity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAdapterFailure(exception))
        {
            return RemediationBaseResult.Refused(SourceWorkspaceCodes.Unavailable);
        }
    }

    public async Task<RemediationApplyResult> ApplyAsync(
        RemediationApplyRequest request,
        CancellationToken cancellationToken)
    {
        if (Resolve(request.Target) is not { } selection)
        {
            return RemediationApplyResult.Refused(RemediationCodes.NotConfigured, answerCorrectable: false);
        }

        try
        {
            var materialized = await MaterializeAsync(selection, cancellationToken);
            using var workspace = materialized.Workspace;
            return workspace is null
                ? RemediationApplyResult.Refused(materialized.Code, answerCorrectable: false)
                : await ApplyToAsync(workspace, request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAdapterFailure(exception))
        {
            return RemediationApplyResult.Refused(SourceWorkspaceCodes.Unavailable, answerCorrectable: false);
        }
    }

    /// <summary>
    /// Checks the base before anything else, then parses, then applies.
    /// </summary>
    /// <remarks>
    /// The order is the point. A hunk that consumes no base line matches at its offset in any file,
    /// so a diff prepared against a tree that no longer exists can apply cleanly to the tree that
    /// replaced it and produce a change nobody approved. The comparison therefore happens before the
    /// text is even parsed: a mismatched base is not a patch problem and must not be reported,
    /// reprompted or repaired as one.
    /// </remarks>
    private async Task<RemediationApplyResult> ApplyToAsync(
        SourceWorkspace workspace,
        RemediationApplyRequest request,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(workspace.TreeIdentity, request.BaseTreeIdentity, StringComparison.Ordinal))
        {
            return RemediationApplyResult.Refused(RemediationCodes.BaseMismatch, answerCorrectable: false);
        }

        var limits = SourcePatchLimits.For(options, SourceWorkspaceBounds.Default);
        var parsed = SourcePatchParser.Parse(request.PatchText, limits);
        if (parsed.Patch is null)
        {
            return RemediationApplyResult.Refused(parsed.Code, IsAnswerCorrectable(parsed.Code));
        }

        var applier = new SourcePatchApplier(workspace.DirectoryPath, limits, SourceWorkspaceBounds.Default);
        var applied = await applier.ApplyAsync(parsed.Patch, cancellationToken);
        return applied.TreeIdentity is null
            ? RemediationApplyResult.Refused(applied.Code, IsAnswerCorrectable(applied.Code))
            : RemediationApplyResult.Applied(applied.TreeIdentity, applied.FilesChanged);
    }

    private Task<SourceWorkspaceResult> MaterializeAsync(
        (SourceContextRootOptions Root, string WorkspaceRoot) selection,
        CancellationToken cancellationToken) =>
        new SourceWorkspaceMaterializer(selection.WorkspaceRoot, SourceWorkspaceBounds.Default)
            .MaterializeAsync(selection.Root.RootPath, cancellationToken);

    /// <summary>
    /// Maps a backend-selected service and release onto a configured root, and pairs it with the
    /// configured workspace root. Either one missing means remediation is not configured here, which
    /// is the shipped state.
    /// </summary>
    private (SourceContextRootOptions Root, string WorkspaceRoot)? Resolve(RemediationTarget target)
    {
        if (string.IsNullOrWhiteSpace(options.WorkspaceRoot))
        {
            return null;
        }

        var root = (options.Roots ?? []).FirstOrDefault(candidate =>
            string.Equals(candidate.ServiceName, target.ServiceName, StringComparison.Ordinal) &&
            string.Equals(candidate.Release, target.Release, StringComparison.Ordinal));
        return root is null ? null : (root, options.WorkspaceRoot);
    }

    /// <summary>
    /// Whether a different diff could plausibly succeed where this one did not.
    /// </summary>
    /// <remarks>
    /// Every refusal the parser and the applier make is about the diff, with two exceptions that are
    /// about the machine underneath it: a filesystem error, and a rollback that could not put the
    /// workspace back. Reprompting on either spends an attempt on a question the model was never
    /// asked, so they are named here rather than inferred from a prefix, and a code added to that
    /// vocabulary later is treated as the model's until someone decides otherwise.
    /// </remarks>
    private static bool IsAnswerCorrectable(string code) =>
        code is not (SourcePatchCodes.Unavailable or SourcePatchCodes.RollbackFailed);

    private static bool IsAdapterFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException;
}
