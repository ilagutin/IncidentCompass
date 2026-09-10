using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Exceptions;
using IncidentCompass.Application.Core.Tenancy;
using IncidentCompass.Application.Intake.FaultGrouping;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Intake.GetFault;

public sealed class GetFaultQueryHandler(IFaultRepository faultRepository, ITriageJobRepository triageJobRepository, IIncidentTenantContext incidentTenantContext)
    : IRequestHandler<GetFaultQuery, FaultDetailsResponse>
{
    public async Task<FaultDetailsResponse> HandleAsync(GetFaultQuery request, CancellationToken cancellationToken)
    {
        var fault = await faultRepository.FindByIdAsync(request.FaultId, await incidentTenantContext.GetTenantIdAsync(cancellationToken), cancellationToken)
            ?? throw new NotFoundException(
                $"Fault '{request.FaultId}' was not found.",
                ApplicationErrorCodes.FaultNotFound,
                "The requested fault does not exist.");
        var job = await triageJobRepository.FindByFaultIdAsync(fault.Id, cancellationToken);
        return new FaultDetailsResponse(
            fault.Id, fault.Status.ToString(), fault.Fingerprint, fault.FingerprintVersion,
            fault.FingerprintStrength.ToString(), fault.CanGroup, fault.ServiceName, fault.Environment,
            fault.Severity, fault.CorrelationId, fault.TriggerSignalId, fault.RecurrenceOf,
            fault.CreatedAtUtc, fault.CompletedAtUtc,
            job is null ? null : ToSummary(job));
    }

    // Only the durable classification code and the scheduled retry time are projected. The sibling
    // LastErrorMessage is deliberately withheld: it is a bounded classification rather than provider
    // text, but its ordinary form appends the raising exception's type name, which is an internal
    // implementation detail that a caller of the public fault endpoint has no reason to receive.
    private static TriageJobSummary ToSummary(TriageJob job) =>
        new(
            job.Id,
            job.Status.ToString(),
            job.Attempt,
            job.ConfigHash,
            job.CreatedAtUtc,
            job.LastErrorCode,
            job.NextAttemptAtUtc);
}
