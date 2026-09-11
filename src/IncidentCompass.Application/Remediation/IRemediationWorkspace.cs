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
/// <b>The third call reads bytes back, and only those the approved diff wrote.</b>
/// <see cref="PrepareForPublicationAsync" /> exists because a push cannot reuse what an approved
/// <c>code_write</c> produced: that call throws its patched tree away by design, and keeping one
/// alive across an approval would mean holding a directory open for days. So a push re-derives the
/// change from base plus patch, which is deterministic, and gets back only the files the diff names.
/// Nothing else crosses: not the rest of the tree, not a host path, not a handle.
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

    /// <summary>
    /// Proves that an approved base is one remote commit, re-applies the approved diff to a fresh
    /// copy of that base, and yields the exact bytes of the files the diff writes. The copy is
    /// discarded before this returns.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing and is the same order <see cref="ApplyAsync" /> uses, with one step
    /// added in front. The copy is identified first and refused unless it is the approved base; then
    /// it is compared with the remote listing and refused unless every shared path holds identical
    /// bytes; only then is the diff parsed and applied. A correspondence checked after the patch
    /// would be a statement about the wrong tree.
    /// </remarks>
    Task<RemediationPublicationResult> PrepareForPublicationAsync(
        RemediationPublicationRequest request,
        CancellationToken cancellationToken);
}
