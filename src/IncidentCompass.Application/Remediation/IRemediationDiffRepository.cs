namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Durable state for produced remediation diffs.
/// </summary>
/// <remarks>
/// Writes are append-only. A diff is a statement about a base that existed at one instant, so a
/// second attempt against a moved checkout is a second record rather than an edit of the first: an
/// updated row would silently repoint an identity a reviewer may already be reading.
/// </remarks>
public interface IRemediationDiffRepository
{
    Task AddAsync(RemediationDiff diff, CancellationToken cancellationToken);

    /// <summary>
    /// Reads back the diffs recorded for one report, tenant-scoped by argument, newest first, and
    /// never more than <paramref name="maximum" /> of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bound is what makes "there is exactly one" answerable without reading a table: an
    /// approval caller asks for two, and two rows means durable state does not name a single change.
    /// </para>
    /// <para>
    /// The tenant is a predicate rather than a filter applied afterwards, for the same reason the
    /// pass context read scopes by one: another tenant's diff must be absent, not present and then
    /// discarded. A report that is not this tenant's therefore yields nothing, which is the same
    /// answer as a report with no diff.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<RemediationDiff>> FindForReportAsync(
        string tenantId,
        Guid reportId,
        int maximum,
        CancellationToken cancellationToken);
}
