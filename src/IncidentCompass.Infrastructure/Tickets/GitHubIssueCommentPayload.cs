namespace IncidentCompass.Infrastructure.Tickets;

/// <summary>
/// The frozen bytes a governed issue comment is approved in, read back into named values.
/// </summary>
/// <remarks>
/// <para>
/// <b>Version 2 added one field, deliberately.</b> A comment used to be five properties, and the shape
/// was enforced in three places: this reader, the tool's argument validation, and the declared argument
/// schema. It is now six, the sixth being <paramref name="PullRequestNumber" />, and all three places
/// moved together. The cost is stated rather than hidden: a comment proposed under version 1 and still
/// waiting for approval when a host upgrades is no longer executable, because this reader refuses it
/// and the dispatch fails closed with a payload-invalid code. That is the intended behaviour for a
/// payload whose shape a person approved and the product no longer reads, and the remedy is a fresh
/// proposal, which the workflow produces on the next evaluation.
/// </para>
/// <para>
/// <b>The number is a number, not a link.</b> Nothing here carries a URL, and the body that renders it
/// writes a bare reference to a number in the same repository the comment is posted to. A provider
/// that echoed something else could not reach a ticket through this shape.
/// </para>
/// </remarks>
/// <param name="OriginReportId">The report the comment is about.</param>
/// <param name="TicketId">The issue number, as the canonical text the payload carries.</param>
/// <param name="IssueNumber">The same value as an integer, for building a request path.</param>
/// <param name="Marker">The at-most-once marker the body ends with and the preflight searches for.</param>
/// <param name="Body">The exact comment text, bounded and backend-composed.</param>
/// <param name="PullRequestNumber">
/// The pull request this comment links to, or <see langword="null" /> when it links to none. A plain
/// governed comment carries null and a backlink carries a number; neither tool accepts the other's
/// value, so the field cannot be used to turn one into the other.
/// </param>
internal sealed record GitHubIssueCommentPayload(
    Guid OriginReportId,
    string TicketId,
    int IssueNumber,
    string Marker,
    string Body,
    string? PullRequestNumber);
