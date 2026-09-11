namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The closed vocabulary the proposal half of a remediation pass reports: what it found in durable
/// state, and what it concluded about it.
/// </summary>
/// <remarks>
/// <para>
/// These sit beside <see cref="RemediationCodes" /> rather than inside it because they answer a
/// different question. Those codes describe preparing a diff; these describe turning a prepared diff
/// into something a human may approve, and every one of them is a refusal to create an approvable
/// proposal. Like the rest, each is a fixed string carrying no path, no line, no byte of a diff and
/// no byte of a file, so all of them are safe to log, persist and return on an intent.
/// </para>
/// <para>
/// Nothing here is a status a model chooses. A model cannot reach this path at all: the proposal is
/// built from durable rows the backend wrote, and the pass's own model call is made with no tools.
/// </para>
/// </remarks>
public static class RemediationProposalCodes
{
    /// <summary>
    /// No diff is recorded for this tenant and this report, so there is nothing to approve. It is
    /// also the signal the workflow reads to decide that the pass has not run yet: the proposal step
    /// runs first, and this code is what sends it to the model rather than to an approval.
    /// </summary>
    public const string DiffMissing = "remediation_diff_missing";

    /// <summary>
    /// More than one diff is recorded for this report, so durable state does not name one change.
    /// </summary>
    /// <remarks>
    /// Refused rather than resolved by a rule such as "newest wins". A person asked to approve the
    /// fix for one report has to be shown one change; picking for them would mean the thing approved
    /// and the thing most recently produced could differ with nothing saying so. The table is
    /// append-only by design, so two rows is a real state, and the honest answer to it is that this
    /// report no longer has an unambiguous fix.
    /// </remarks>
    public const string DiffAmbiguous = "remediation_diff_ambiguous";

    /// <summary>
    /// A diff was found for the report but it does not belong to the attempt that published it: a
    /// different job, a different attempt, a different service or a different release.
    /// </summary>
    /// <remarks>
    /// Another tenant's or another report's diff never reaches this check, because the read is
    /// scoped by both and returns nothing; that case is <see cref="DiffMissing" />. This one catches
    /// a row that is inconsistent with the origin it claims, which the schema cannot express.
    /// </remarks>
    public const string DiffForeign = "remediation_diff_foreign";

    /// <summary>
    /// The diff does not carry the one shape this release knows how to freeze: it did not apply
    /// whole, or it claims something about a test.
    /// </summary>
    /// <remarks>
    /// This is the guard that keeps an untested artifact and a tested one from being confused. Every
    /// diff this release can produce carries <c>not_executed</c> and no test command, and the frozen
    /// payload says so in those words. A row carrying a real test outcome is a row written by
    /// something this payload shape cannot describe, so it is refused rather than folded into a
    /// statement that would then be false.
    /// </remarks>
    public const string DiffUnsupported = "remediation_diff_unsupported";

    /// <summary>
    /// The monitored checkout is no longer the tree the diff was prepared against, so the change is
    /// about a tree that no longer exists and nothing may be approved for it.
    /// </summary>
    public const string BaseStale = "remediation_base_stale";

    /// <summary>
    /// The payload the diff would be frozen into does not fit the action-payload ceiling.
    /// </summary>
    /// <remarks>
    /// The raw diff budget is derived from that ceiling with a fixed reserve for the fields that
    /// travel beside it, so this is normally unreachable. It is measured anyway, because the reserve
    /// is sized for an ordinary envelope and a service name or release made entirely of characters
    /// the canonical writer escapes to six bytes each can exceed it. A measured refusal is better
    /// than an arithmetic promise.
    /// </remarks>
    public const string PayloadOversized = "remediation_proposal_payload_oversized";

    /// <summary>
    /// A proposal already exists for this report under the same key with different immutable input.
    /// </summary>
    /// <remarks>
    /// Reachable when the host binding behind the workspace changed between two attempts: the diff
    /// is the same but what it is bound to is not. It is a settled outcome, not a retry, because the
    /// frozen proposal a person may already be reading must not be repointed.
    /// </remarks>
    public const string ProposalConflict = "remediation_proposal_conflict";

    /// <summary>One proposal was created or replayed, and it is waiting for a person.</summary>
    public const string ProposalRequested = "remediation_proposal_requested";

    /// <summary>
    /// The proposal was created already approved, which this release never expects for a
    /// <c>code_write</c> action. It is a distinct code so that a configuration which somehow
    /// auto-approved a code write is visible on the intent rather than indistinguishable from the
    /// normal outcome.
    /// </summary>
    public const string ProposalAutoApproved = "remediation_proposal_auto_approved";
}
