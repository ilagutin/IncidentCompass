using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Investigation.Jobs;

internal sealed class DeferredClaimedTriageJobProcessor : IClaimedTriageJobProcessor
{
    public Task ProcessAsync(
        TriageJob job,
        TriageConfiguration configuration,
        string workerId,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException(
            "The governed triage job processor is not wired. AddInfrastructure provides the orchestrator loop.");
    }
}
