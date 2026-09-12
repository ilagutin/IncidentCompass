namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Everything a <c>code_write</c> proposal freezes about one remediation diff, in the one shape that
/// is written into the approval payload and read back out of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is here and why.</b> Every field is something that can change the tree an approval
/// authorizes, plus the two statements about what has not been checked. The diff is the change; the
/// base identity is the tree it is a change to; the service and release are which checkout that tree
/// is; the evidence identity is what the change was derived from; and the test fields say that
/// nothing was run. An approval hash over these bytes therefore covers every input to
/// <c>base + patch</c>.
/// </para>
/// <para>
/// <b>What is deliberately absent.</b> The identity of the <c>remediation_diffs</c> row, when it was
/// written, and the route and model that produced it. None of them can change the resulting tree,
/// and including the row id would be worse than useless: two passes that derived the same change
/// would freeze two different payloads, so an idempotent proposal would become one proposal per pass.
/// Provenance of that kind belongs in the diff table and the ledger, which is where it is.
/// </para>
/// <para>
/// <b>There is no branch, remote, repository URL or credential here, and there is nowhere for one to
/// come from.</b> The service and release are backend-selected values that a host maps to a
/// directory; nothing above the workspace port knows what that directory is, and nothing in the
/// product reads git.
/// </para>
/// </remarks>
/// <param name="OriginReportId">The current completed report the change was derived from.</param>
/// <param name="ServiceName">The faulting service, and half of what selects the checkout.</param>
/// <param name="Release">The configured current release, and the other half.</param>
/// <param name="BaseTreeIdentity">
/// The content identity of the tree the diff was prepared against. Execution refuses when the tree it
/// materializes is not this one, which is the whole of what binds an insert-only hunk to a tree.
/// </param>
/// <param name="ResultTreeIdentity">
/// The content identity the diff produced when it was applied. Execution recomputes it and refuses a
/// difference, so the approved bytes are proved sufficient rather than assumed to be.
/// </param>
/// <param name="FilesChanged">How many file sections the diff writes.</param>
/// <param name="PatchBytes">Raw UTF-8 bytes of <see cref="PatchText" />.</param>
/// <param name="PatchText">The exact unified diff, byte for byte as it applied.</param>
/// <param name="EvidenceSha256">
/// A digest over the cited source artifacts the change was derived from. It is a digest rather than
/// the list because the list is unbounded in width while the payload's reserve is not, and because
/// what an approval needs is that the evidence set is exactly the one that was reviewed, which a
/// digest states in 64 characters.
/// </param>
/// <param name="EvidenceCount">How many artifacts that digest covers.</param>
/// <param name="TestOutcome">
/// Always <see cref="RemediationDiff.TestNotExecuted" /> in this release. A payload carrying anything
/// else is refused before it is built, so this field can never be a way to describe a real result in
/// a shape that says nothing ran.
/// </param>
internal sealed record RemediationProposalPayload(
    Guid OriginReportId,
    string ServiceName,
    string Release,
    string BaseTreeIdentity,
    string ResultTreeIdentity,
    int FilesChanged,
    int PatchBytes,
    string PatchText,
    string EvidenceSha256,
    int EvidenceCount,
    string TestOutcome);
