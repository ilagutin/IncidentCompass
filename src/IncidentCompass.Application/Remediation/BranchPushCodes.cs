namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The closed vocabulary of turning an executed, approved code write into an approvable branch push,
/// and of executing one.
/// </summary>
/// <remarks>
/// Every value is a fixed string carrying no repository, branch, path, credential or byte of a file.
/// Nothing here is a status a model chooses: the whole input is durable state the backend wrote plus
/// a listing the backend read.
/// </remarks>
public static class BranchPushCodes
{
    /// <summary>
    /// No executed code write exists for this report, so there is nothing to publish. It is also the
    /// state an intent evaluated before its predecessor settled would see, and it is settled rather
    /// than retried: the intent that schedules a push is written by the predecessor's own terminal
    /// transaction, so an evaluation that finds nothing is looking at a report whose code write
    /// failed, was superseded or was rolled back, not at one that has not finished.
    /// </summary>
    public const string PredecessorMissing = "branch_push_predecessor_missing";

    /// <summary>
    /// The predecessor's frozen payload is not a shape this release can publish.
    /// </summary>
    public const string PredecessorPayloadInvalid = "branch_push_predecessor_payload_invalid";

    /// <summary>The push is not enabled in the job's own configuration snapshot.</summary>
    public const string Disabled = "branch_push_disabled";

    /// <summary>The intent was malformed, so no push was proposed and none can be.</summary>
    public const string IntentInvalid = "branch_push_intent_invalid";

    /// <summary>The frozen payload an approval was taken over is not the shape this release executes.</summary>
    public const string PayloadInvalid = "branch_push_payload_invalid";

    /// <summary>
    /// The proposal would exceed the action-payload ceiling. Measured rather than assumed, because
    /// the reserve is sized for an ordinary envelope.
    /// </summary>
    public const string PayloadOversized = "branch_push_payload_oversized";

    /// <summary>
    /// A proposal already exists for this report under the same key with different immutable input.
    /// Settled, not retried: the frozen proposal a person may be reading must not be repointed.
    /// </summary>
    public const string ProposalConflict = "branch_push_proposal_conflict";

    /// <summary>One proposal was created or replayed, and it is waiting for a person.</summary>
    public const string ProposalRequested = "branch_push_proposal_requested";

    /// <summary>
    /// The proposal was created already approved, which no shipped policy allows for this category.
    /// A distinct code so that such a configuration is visible rather than indistinguishable from the
    /// normal outcome.
    /// </summary>
    public const string ProposalAutoApproved = "branch_push_proposal_auto_approved";

    /// <summary>
    /// The base, the branch or the repository the approval names is not what the host is bound to
    /// now, so the approval describes a push that would land somewhere else.
    /// </summary>
    public const string BindingChanged = "branch_push_binding_changed";

    /// <summary>The sibling action history is longer than this adapter reads, so it cannot be trusted.</summary>
    public const string HistoryExceeded = "branch_push_history_exceeded";

    /// <summary>Another push for this report is proposed or approved and has not settled.</summary>
    public const string PriorActionPending = "branch_push_prior_action_pending";

    /// <summary>
    /// An earlier push for this report ended with an unknown outcome and the branch it would have
    /// created does not exist. A person decides what happens next; nothing is retried automatically.
    /// </summary>
    public const string PriorOutcomeUnknown = "branch_push_prior_outcome_unknown";

    /// <summary>
    /// An earlier executed push recorded a commit this one cannot confirm, so durable state and the
    /// provider disagree and nothing is written.
    /// </summary>
    public const string PriorResultInvalid = "branch_push_prior_result_invalid";

    /// <summary>
    /// The base was re-proved at dispatch and the proof is not the one the approval froze, so the
    /// approval describes a change to something other than what is there now.
    /// </summary>
    public const string CorrespondenceChanged = "branch_push_correspondence_changed";
}
