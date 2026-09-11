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
}
