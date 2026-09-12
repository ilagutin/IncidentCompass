namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The approved, executed <c>code_write</c> action a branch push continues.
/// </summary>
/// <remarks>
/// This is the whole of what a successor is allowed to know about its predecessor: which action it
/// was, the exact bytes a person approved, and a digest of what executing them produced. It is read
/// from <c>action_approvals</c> directly, so the successor's own proposal cannot be built unless that
/// row is in the one state that makes a push meaningful.
/// </remarks>
/// <param name="ActionId">The predecessor action row.</param>
/// <param name="CanonicalPayload">
/// The exact frozen bytes the person approved. The push re-reads the diff, the base and the result
/// out of these rather than out of the diff table, so the change that is published is the change that
/// was approved and not merely one derived from the same report.
/// </param>
/// <param name="ResultSha256">
/// A digest over the predecessor's canonical result. Freezing it into the successor's payload is what
/// binds the two: an approval to push is an approval to push the outcome of that exact execution.
/// </param>
/// <param name="ExecutedAtUtc">
/// When the predecessor completed. It is the instant the push pins into its commit's author and
/// committer dates, and it is taken from durable state rather than from a clock so that two
/// evaluations of the same intent freeze identical bytes. A wall clock here would make a retried
/// evaluation a payload conflict instead of a replay, and would make the commit id depend on when the
/// queue happened to run.
/// </param>
public sealed record RemediationPredecessor(
    Guid ActionId,
    byte[] CanonicalPayload,
    string ResultSha256,
    DateTimeOffset ExecutedAtUtc)
{
    /// <summary>
    /// The identifier the predecessor recorded in its own compact audit projection, or
    /// <see langword="null" /> when it recorded none.
    /// </summary>
    /// <remarks>
    /// It is read from the projection column rather than parsed out of the predecessor's result
    /// document, which is exactly what that projection exists for. A pull request needs the commit an
    /// approved push confirmed, and "confirmed" has to mean a value the terminal transaction wrote
    /// under a check constraint, not a field a reader hoped to find in JSON.
    /// </remarks>
    public string? ExternalResourceId { get; init; }

    /// <summary>
    /// The origin report's own recorded confidence, or <see langword="null" /> when it could not be
    /// read. It is the one statement of uncertainty a published description makes, and it comes from
    /// the report row rather than from anything a successor composed.
    /// </summary>
    public string? ReportConfidence { get; init; }
}
