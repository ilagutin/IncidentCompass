namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Which monitored checkout a remediation pass is about, named the way the source-read boundary
/// already names one: a service and the release the snapshotted configuration says is current.
/// </summary>
/// <remarks>
/// Both values are backend-selected. The service comes from the fault, the release from the job's
/// own configuration snapshot, and neither is ever taken from model text. A host maps the pair to a
/// root; nothing above the port knows what that root is.
/// </remarks>
/// <param name="ServiceName">The faulting service, as intake recorded it.</param>
/// <param name="Release">The configured current release for that service.</param>
public sealed record RemediationTarget(string ServiceName, string Release);
