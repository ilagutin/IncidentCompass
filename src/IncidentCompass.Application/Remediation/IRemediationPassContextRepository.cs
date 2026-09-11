namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Reads back everything one remediation pass runs on, for one published report.
/// </summary>
/// <remarks>
/// Read only, and tenant-scoped by argument rather than by ambient context: the caller is a
/// background workflow acting on a durable intent, and the tenant on that intent is the one the
/// report must belong to. A report that is not that tenant's is not found, which is the same answer
/// as a report that does not exist.
/// </remarks>
internal interface IRemediationPassContextRepository
{
    Task<RemediationPassContext?> FindAsync(
        string tenantId,
        Guid reportId,
        CancellationToken cancellationToken);
}
