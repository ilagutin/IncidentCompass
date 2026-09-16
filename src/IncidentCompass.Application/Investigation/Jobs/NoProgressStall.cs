using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>What the no-progress handler needs from the attempt it may recover or terminate.</summary>
/// <param name="Job">The claimed job.</param>
/// <param name="Configuration">The job's configuration snapshot.</param>
/// <param name="Context">The job's investigation context; its job-level artifacts are the backend report's evidence.</param>
/// <param name="WorkerId">The worker holding the lease, needed to publish.</param>
/// <param name="AttemptStartedAtUtc">When the attempt started, for the recovery call's duration budget.</param>
/// <param name="Messages">The orchestrator conversation a recovery suggestion is appended to.</param>
/// <param name="Progress">The attempt's progress record.</param>
internal sealed record NoProgressStall(
    TriageJob Job,
    TriageConfiguration Configuration,
    TriageJobInvestigationContext Context,
    string WorkerId,
    DateTimeOffset AttemptStartedAtUtc,
    List<AiChatMessage> Messages,
    InvestigationProgressTracker Progress);
