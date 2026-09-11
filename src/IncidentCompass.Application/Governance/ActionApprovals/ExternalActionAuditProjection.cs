using System.Text;
using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.Application.Governance.ActionApprovals;

public sealed record ExternalActionAuditProjection(
    string ResourceKind,
    string ResourceId,
    string BeforeState,
    string AfterState)
{
    public const string TelegramMessageKind = "telegram_message";
    public const string GitHubIssueKind = "github_issue";

    /// <summary>
    /// A branch this product created, named by the commit it was created at.
    /// </summary>
    /// <remarks>
    /// The commit is the identity rather than the branch name for two reasons. It is the fact that
    /// cannot be recomputed: the branch name is derived from the origin report, which is already a
    /// column on the same row, while the commit exists only because the push happened. And it keeps
    /// the column a fixed-width identifier: a branch name is free-form text of unbounded shape, and a
    /// projection whose whole purpose is to be compact and safe to index should not become the place
    /// arbitrary names are stored.
    /// </remarks>
    public const string GitBranchKind = "git_branch";

    /// <summary>Characters in a git object name.</summary>
    public const int GitObjectNameCharacters = 40;

    public const int MaximumStateBytes = 2048;

    public static ExternalActionAuditProjection TelegramMessage(string messageId) =>
        new(TelegramMessageKind, messageId, "not_sent", "sent");

    public static ExternalActionAuditProjection GitHubIssueCreated(string issueNumber) =>
        new(GitHubIssueKind, issueNumber, "absent", "open");

    public static ExternalActionAuditProjection GitHubIssueCommentAdded(string issueNumber) =>
        new(GitHubIssueKind, issueNumber, "open", "comment_added");

    public static ExternalActionAuditProjection GitBranchPushed(string commitSha) =>
        new(GitBranchKind, commitSha, "absent", "created");

    public void Validate()
    {
        if (!IsValidResourceIdentity(ResourceKind, ResourceId) ||
            Encoding.UTF8.GetByteCount(BeforeState) is < 1 or > MaximumStateBytes ||
            Encoding.UTF8.GetByteCount(AfterState) is < 1 or > MaximumStateBytes ||
            !HasClosedStateTransition())
        {
            throw new ActionProposalValidationException(
                "External action audit projection is invalid or exceeds its bound.");
        }
    }

    /// <summary>
    /// Whether a kind and an id name a resource this projection can hold.
    /// </summary>
    /// <remarks>
    /// The two identifier shapes are per kind rather than shared. A provider that numbers its
    /// resources gives a positive integer; a git branch is named here by the commit it was created
    /// at, which is a git object name. Widening the integer rule to admit hexadecimal would have let
    /// an issue number that is not a number through, so each kind states its own shape.
    /// </remarks>
    public static bool IsValidResourceIdentity(string? resourceKind, string? resourceId) =>
        resourceKind switch
        {
            TelegramMessageKind or GitHubIssueKind =>
                resourceId is { Length: >= 1 and <= 20 } &&
                resourceId[0] is >= '1' and <= '9' &&
                resourceId.All(static character => character is >= '0' and <= '9'),
            GitBranchKind =>
                resourceId is { Length: GitObjectNameCharacters } &&
                resourceId.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            _ => false
        };

    internal bool Matches(ActionCategory category) => category switch
    {
        ActionCategory.Notification =>
            ResourceKind == TelegramMessageKind && BeforeState == "not_sent" && AfterState == "sent",
        ActionCategory.TicketCreate =>
            ResourceKind == GitHubIssueKind && BeforeState == "absent" && AfterState == "open",
        ActionCategory.TicketUpdate =>
            ResourceKind == GitHubIssueKind && BeforeState == "open" && AfterState == "comment_added",
        ActionCategory.BranchPush =>
            ResourceKind == GitBranchKind && BeforeState == "absent" && AfterState == "created",
        _ => false
    };

    private bool HasClosedStateTransition() =>
        ResourceKind == TelegramMessageKind && BeforeState == "not_sent" && AfterState == "sent" ||
        ResourceKind == GitHubIssueKind && BeforeState == "absent" && AfterState == "open" ||
        ResourceKind == GitHubIssueKind && BeforeState == "open" && AfterState == "comment_added" ||
        ResourceKind == GitBranchKind && BeforeState == "absent" && AfterState == "created";
}
