namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Reads the one executed <c>code_write</c> action a report may have, which is the only thing a
/// branch push is ever allowed to continue.
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
}
