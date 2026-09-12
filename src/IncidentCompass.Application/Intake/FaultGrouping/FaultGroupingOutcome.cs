using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Intake.FaultGrouping;

// Job is null only in the "attached to existing open fault" and "attached to closed fault,
// suppressed" branches, where the coordinator performs no job lookup. A later change may choose
// to look up the existing job for those branches too; intake does not require it today.
public sealed record FaultGroupingOutcome(Fault Fault, TriageJob? Job, bool IsNewFault, bool IsNewJob, bool IsSuppressed);
