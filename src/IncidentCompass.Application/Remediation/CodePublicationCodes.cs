namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The closed vocabulary of reaching a code-hosting provider to read a base and push one branch.
/// </summary>
/// <remarks>
/// Every value is a fixed string. None carries a repository, a branch, a token, a URL or a byte of a
/// file, because these are logged and persisted on an action row and the binding behind them is host
/// configuration a reader has no business reconstructing from a failure code.
/// </remarks>
public static class CodePublicationCodes
{
    /// <summary>The base commit, its tree and its file listing were read.</summary>
    public const string BaseRead = "code_publication_base_read";

    /// <summary>The branch was created at the commit this dispatch derived.</summary>
    public const string BranchCreated = "code_publication_branch_created";

    /// <summary>
    /// The branch exists and the commit it points at was read. Only ever an answer to a read: a read
    /// creates nothing, so it must not borrow <see cref="BranchCreated" />. The code is logged and
    /// persisted on an action row, where the difference between "this dispatch created a reference"
    /// and "this dispatch looked at one" is the whole at-most-once story.
    /// </summary>
    public const string BranchRead = "code_publication_branch_read";

    /// <summary>
    /// The branch already points at exactly the commit this dispatch derived, so the push had
    /// already happened and nothing was written. This is the replay answer, not a failure.
    /// </summary>
    public const string BranchAlreadyAtCommit = "code_publication_branch_already_at_commit";

    /// <summary>The branch does not exist. Only ever an answer to a read, never to a push.</summary>
    public const string BranchAbsent = "code_publication_branch_absent";

    /// <summary>
    /// The branch exists and points somewhere else. Refused: this product never moves a reference it
    /// did not create, and has no operation that could.
    /// </summary>
    public const string BranchDiverged = "code_publication_branch_diverged";

    /// <summary>No owner, repository or credential is configured on this host.</summary>
    public const string BindingUnavailable = "code_publication_binding_unavailable";

    /// <summary>The configured base branch does not exist in the configured repository.</summary>
    public const string BaseBranchMissing = "code_publication_base_branch_missing";

    /// <summary>
    /// The provider truncated the recursive tree listing, so the comparison would be against a
    /// subset of the remote tree chosen by the provider. Refused rather than narrowed.
    /// </summary>
    public const string BaseTreeTruncated = "code_publication_base_tree_truncated";

    /// <summary>
    /// The base tree holds an entry this product cannot compare or reproduce: a symlink, a submodule
    /// or any other non-regular file. Refused, because a push built on it would carry content the
    /// approved base never described.
    /// </summary>
    public const string BaseTreeUnsupportedEntry = "code_publication_base_tree_unsupported_entry";

    /// <summary>
    /// The commit the provider built is not the commit this dispatch asked for: its tree, its parent
    /// or its message differs. Refused before the reference is touched.
    /// </summary>
    public const string CommitMismatch = "code_publication_commit_mismatch";

    /// <summary>One pull request was opened for the approved head.</summary>
    public const string PullRequestOpened = "code_publication_pull_request_opened";

    /// <summary>
    /// A pull request for exactly this head and this base already exists, so the create had already
    /// happened and nothing was written. This is the replay answer, not a failure.
    /// </summary>
    public const string PullRequestAlreadyOpen = "code_publication_pull_request_already_open";

    /// <summary>
    /// No pull request exists for this head. Only ever an answer to a read, never to a create.
    /// </summary>
    public const string PullRequestAbsent = "code_publication_pull_request_absent";

    /// <summary>
    /// More than one pull request answers for this head. The head is derived from the origin report
    /// and only this product creates it, so two is a state a person resolves rather than one an
    /// adapter picks from.
    /// </summary>
    public const string PullRequestAmbiguous = "code_publication_pull_request_ambiguous";

    /// <summary>
    /// The pull request the provider named is not the one that was asked about: its head reference,
    /// its head commit or its base branch differs. Refused rather than adopted.
    /// </summary>
    public const string PullRequestMismatch = "code_publication_pull_request_mismatch";

    /// <summary>
    /// The head branch a pull request would be opened from does not exist. Nothing is created: a
    /// pull request is only ever opened from a reference an earlier approved push confirmed.
    /// </summary>
    public const string HeadBranchMissing = "code_publication_head_branch_missing";

    /// <summary>
    /// The head branch exists and points somewhere other than the commit the approval named, so the
    /// approval describes publishing a change other than the one that was reviewed.
    /// </summary>
    public const string HeadBranchDiverged = "code_publication_head_branch_diverged";

    /// <summary>The credential was rejected.</summary>
    public const string AuthenticationFailed = "code_publication_authentication_failed";

    /// <summary>The credential is valid and lacks the access this operation needs.</summary>
    public const string Forbidden = "code_publication_forbidden";

    /// <summary>The provider rate-limited the request.</summary>
    public const string RateLimited = "code_publication_rate_limited";

    /// <summary>The configured repository was not found by the configured credential.</summary>
    public const string RepositoryNotFound = "code_publication_repository_not_found";

    /// <summary>The provider rejected the request as malformed.</summary>
    public const string RequestInvalid = "code_publication_request_invalid";

    /// <summary>The provider answered with something this adapter cannot read.</summary>
    public const string ResponseMalformed = "code_publication_response_malformed";

    /// <summary>The provider could not be reached, or answered with a transient failure.</summary>
    public const string Unavailable = "code_publication_unavailable";

    /// <summary>
    /// Cancellation was observed after the content-addressed objects were built and before the
    /// reference create was sent, so nothing changed. It is its own code rather than an unknown
    /// outcome, because an unknown outcome is a state a person has to settle and this is not one.
    /// </summary>
    public const string CancelledBeforeWrite = "code_publication_cancelled_before_write";

    /// <summary>
    /// The reference create was sent and its outcome is not known. Durable, never automatically
    /// repeated, and reconciled by one read of the branch reference.
    /// </summary>
    public const string OutcomeUnknown = "dispatch_outcome_unknown";
}
