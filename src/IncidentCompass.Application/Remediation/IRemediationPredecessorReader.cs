namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Reads the one executed predecessor a report may have for each link in the publication chain: the
/// <c>code_write</c> a branch push continues, and the <c>branch_push</c> a pull request continues.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where the ordering is checked the second time.</b> The first check is structural: the
/// intent that schedules a push is written in the same database transaction that records the
/// <c>code_write</c> action as executed, so it cannot exist earlier. This one re-reads that row when
/// the push is proposed, because an intent can be evaluated later than it was written and a reader
/// that trusted its own existence would be trusting a fact it never verified. The third check is the
/// approval hash: the predecessor's id and result digest go into the frozen payload, so an approval to
/// push is an approval to push that execution and cannot be re-pointed at another.
/// </para>
/// <para>
/// <b>Ambiguity refuses.</b> More than one executed <c>code_write</c> for one report means durable
/// state does not name one change, and picking would mean publishing something a person was never
/// shown. The reader returns nothing and the push is refused.
/// </para>
/// </remarks>
public interface IRemediationPredecessorReader
{
    Task<RemediationPredecessor?> FindExecutedCodeWriteAsync(
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the one executed, live <c>branch_push</c> action a report may have, together with the
    /// commit that action recorded in its own audit projection and the report's recorded confidence.
    /// </summary>
    /// <remarks>
    /// The three values travel together because a pull request needs all three at once and each is a
    /// fact about the same row or the row it points at. Reading them in one place keeps the second link
    /// in the chain checked the same three ways the first one is: the intent exists only because the
    /// push's terminal transaction wrote it, this read re-verifies that the row is in the one state
    /// that makes a pull request meaningful, and the push's identity and result digest go into the
    /// successor's frozen payload.
    /// </remarks>
    Task<RemediationPredecessor?> FindExecutedBranchPushAsync(
        string tenantId,
        Guid originReportId,
        CancellationToken cancellationToken);
}
