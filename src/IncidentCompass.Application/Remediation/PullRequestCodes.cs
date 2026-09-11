namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The closed vocabulary of turning an executed, approved branch push into an approvable pull request,
/// and of executing one.
/// </summary>
/// <remarks>
/// Every value is a fixed string carrying no repository, branch, path, credential, title, body or byte
/// of a file. Nothing here is a status a model chooses: the whole input is durable state the backend
/// wrote plus a read the backend made.
/// </remarks>
public static class PullRequestCodes
{
    /// <summary>
    /// No executed branch push exists for this report, so there is no head to open a pull request
    /// from. It is settled rather than retried: the intent that schedules a pull request is written by
    /// the push's own terminal transaction, so an evaluation that finds nothing is looking at a report
    /// whose push failed, was superseded or was rolled back, not at one that has not finished.
    /// </summary>
    public const string PredecessorMissing = "pr_create_predecessor_missing";

    /// <summary>The predecessor's frozen payload is not a shape this release can publish.</summary>
    public const string PredecessorPayloadInvalid = "pr_create_predecessor_payload_invalid";

    /// <summary>
    /// The executed push recorded no usable commit in its own audit projection, so there is no head
    /// this release can prove was pushed, and it will not open a pull request over a guess.
    /// </summary>
    public const string PredecessorHeadUnconfirmed = "pr_create_predecessor_head_unconfirmed";

    /// <summary>
    /// The origin report's own recorded confidence is not one of the closed values this release
    /// knows, so the one statement of uncertainty the description makes could not be made truthfully.
    /// Nothing is proposed rather than a description being published without it.
    /// </summary>
    public const string OriginReportUnreadable = "pr_create_origin_report_unreadable";

    /// <summary>The pull request is not enabled in the job's own configuration snapshot.</summary>
    public const string Disabled = "pr_create_disabled";

    /// <summary>The intent was malformed, so nothing was proposed and nothing can be.</summary>
    public const string IntentInvalid = "pr_create_intent_invalid";

    /// <summary>The frozen payload an approval was taken over is not the shape this release executes.</summary>
    public const string PayloadInvalid = "pr_create_payload_invalid";

    /// <summary>
    /// The proposal would exceed the action-payload ceiling. Measured rather than assumed.
    /// </summary>
    public const string PayloadOversized = "pr_create_payload_oversized";

    /// <summary>
    /// A proposal already exists for this report under the same key with different immutable input.
    /// Settled, not retried: the frozen proposal a person may be reading must not be repointed.
    /// </summary>
    public const string ProposalConflict = "pr_create_proposal_conflict";

    /// <summary>One proposal was created or replayed, and it is waiting for a person.</summary>
    public const string ProposalRequested = "pr_create_proposal_requested";

    /// <summary>
    /// The proposal was created already approved, which no shipped policy allows for this category.
    /// A distinct code so that such a configuration is visible rather than indistinguishable from the
    /// normal outcome.
    /// </summary>
    public const string ProposalAutoApproved = "pr_create_proposal_auto_approved";

    /// <summary>
    /// The base or the repository the approval names is not what the host is bound to now, so the
    /// approval describes a pull request that would land somewhere else.
    /// </summary>
    public const string BindingChanged = "pr_create_binding_changed";
}
