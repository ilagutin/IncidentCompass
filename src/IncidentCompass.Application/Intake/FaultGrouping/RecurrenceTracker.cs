using IncidentCompass.Application.Intake.Artifacts;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Intake.FaultGrouping;

public sealed class RecurrenceTracker(IRecurrenceStateRepository recurrenceStateRepository)
{
    public Task<RecurrenceState?> TrackAsync(
        Fault fault,
        Signal triggerSignal,
        TriageJob job,
        FaultGroupingSettings settings,
        CancellationToken cancellationToken)
    {
        if (fault.RecurrenceOf is null || !triggerSignal.CanGroup)
        {
            return Task.FromResult<RecurrenceState?>(null);
        }

        var recurrence = new RecurrenceOccurrence(
            job.Id,
            fault.Id,
            triggerSignal.TenantId,
            triggerSignal.ServiceName,
            triggerSignal.Environment,
            triggerSignal.Fingerprint!,
            triggerSignal.FingerprintVersion!.Value,
            triggerSignal.GroupingRuleId,
            triggerSignal.GroupingRuleVersion,
            triggerSignal.ObservedAtUtc,
            settings.Recurrence?.EscalateAfterCount ?? 0);
        return RecordAsync(recurrence, cancellationToken);
    }

    public async Task<RecurrenceAttachmentResult?> TrackAttachmentAsync(
        Fault fault,
        Signal signal,
        ITriageJobRepository triageJobRepository,
        GroundedFactsAssembler groundedFactsAssembler,
        FaultGroupingSettings settings,
        CancellationToken cancellationToken)
    {
        if (fault.RecurrenceOf is null)
        {
            return null;
        }

        var job = await triageJobRepository.FindByFaultIdAsync(fault.Id, cancellationToken)
            ?? throw new InvalidOperationException("An open recurrence fault must have a triage job.");
        var recurrenceState = await TrackAsync(fault, signal, job, settings, cancellationToken);
        if (recurrenceState is null)
        {
            return null;
        }

        await groundedFactsAssembler.ReplaceRecurrenceStateAsync(job, recurrenceState, cancellationToken);
        return new RecurrenceAttachmentResult(job, recurrenceState);
    }

    private async Task<RecurrenceState?> RecordAsync(
        RecurrenceOccurrence recurrence,
        CancellationToken cancellationToken) =>
        await recurrenceStateRepository.RecordAsync(recurrence, cancellationToken);
}
