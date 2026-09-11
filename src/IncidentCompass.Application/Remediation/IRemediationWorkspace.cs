namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The port a remediation pass reaches the monitored checkout through. It names the base a change is
/// prepared against, and it applies one candidate diff to a disposable copy of that base.
/// </summary>
/// <remarks>
/// <para>
/// <b>What does not cross it.</b> A caller names a service and a release, both backend-selected, and
/// gets back a content identity. It never learns where the checkout is, where the copy went, or that
/// a copy happened at all, and no host path, file body or handle travels either way. An adapter owns
/// the workspace for the length of one call and disposes it before returning, so no lease and no
/// lifetime cross the boundary.
/// </para>
/// <para>
/// <b>Two calls rather than one, and why the base identity is an input.</b>
/// <see cref="IdentifyBaseAsync" /> names the tree the model is asked to write against, and
/// <see cref="ApplyAsync" /> is told that name and must refuse when the tree it materializes is
/// something else. A hunk that consumes no base line quotes no base line, so an insert-only patch
/// matches at its offset in any file and the patch text binds a change to no particular tree; the
/// identity is the only thing that does. Making it an input rather than an output also means one
/// call shape serves both this pass, where the identity comes from what was just measured, and a
/// later application of an approved patch, where it comes from what a human approved.
/// </para>
/// <para>
/// <b>Nothing behind this port executes anything.</b> A workspace is written and read; no process is
/// started and no file in it is run.
/// </para>
/// </remarks>
public interface IRemediationWorkspace
{
    /// <summary>
    /// Names the tree a change would be prepared against, without changing anything.
    /// </summary>
    Task<RemediationBaseResult> IdentifyBaseAsync(
        RemediationTarget target,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies one candidate diff to a fresh copy of the target, refusing when that copy is not the
    /// base the caller named. The copy is discarded before this returns: what survives is the two
    /// identities and the outcome, not the patched tree.
    /// </summary>
    Task<RemediationApplyResult> ApplyAsync(
        RemediationApplyRequest request,
        CancellationToken cancellationToken);
}
