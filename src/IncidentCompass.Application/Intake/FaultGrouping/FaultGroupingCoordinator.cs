using IncidentCompass.Application.Intake.Artifacts;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Exceptions;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Intake.FaultGrouping;

public sealed class FaultGroupingCoordinator(
    ISignalRepository signalRepository,
    IFaultRepository faultRepository,
    ITriageJobRepository triageJobRepository,
    IIntakeUnitOfWork intakeUnitOfWork,
    RecurrenceTracker recurrenceTracker,
    RecurrenceEscalationScheduler recurrenceEscalationScheduler,
    GroundedFactsAssembler groundedFactsAssembler,
    OpenFaultNeighborSetRefresher neighborSetRefresher,
    TimeProvider timeProvider)
{
    private const int MaxResolutionAttempts = 3;
    public async Task<FaultGroupingOutcome> ResolveAsync(
        Signal draftSignal,
        TriageConfiguration configuration,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ResolveCoreAsync(draftSignal, configuration, cancellationToken);
            }
            catch (FaultGroupingStateChangedException) when (attempt < MaxResolutionAttempts)
            {
                // The intake transaction was rolled back. Re-read grouping state after the
                // concurrent fault terminalization commits.
            }
        }
    }

    private async Task<FaultGroupingOutcome> ResolveCoreAsync(
        Signal draftSignal,
        TriageConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var settings = configuration.FaultGrouping;
        var now = timeProvider.GetUtcNow();
        var suppressionPolicy = SuppressionPolicyResolver.Resolve(settings, draftSignal);
        draftSignal = draftSignal with
        {
            SuppressionRuleId = suppressionPolicy.Id,
            EffectiveSuppressionWindowMinutes = suppressionPolicy.SilenceWindowMinutes
        };
        if (draftSignal.FingerprintStrength == FingerprintStrength.Weak)
        {
            return await CreateNewFaultAsync(draftSignal, recurrenceOfFaultId: null, configuration, now, cancellationToken);
        }
        var openFault = await faultRepository.FindOpenFaultAsync(
            draftSignal.TenantId, draftSignal.ServiceName, draftSignal.Environment,
            draftSignal.Fingerprint!, draftSignal.FingerprintVersion!.Value,
            draftSignal.GroupingRuleId, draftSignal.GroupingRuleVersion, cancellationToken);
        if (openFault is not null)
        {
            return await AttachToOpenFaultAsync(draftSignal, openFault, configuration, cancellationToken);
        }
        var closedFault = await faultRepository.FindMostRecentClosedFaultAsync(
            draftSignal.TenantId, draftSignal.ServiceName, draftSignal.Environment,
            draftSignal.Fingerprint!, draftSignal.FingerprintVersion!.Value,
            draftSignal.GroupingRuleId, draftSignal.GroupingRuleVersion, cancellationToken);
        if (closedFault is not null &&
            closedFault.CompletedAtUtc is not null &&
            closedFault.CompletedAtUtc.Value >= now.AddMinutes(-draftSignal.EffectiveSuppressionWindowMinutes!.Value))
        {
            var finalSignal = draftSignal with
            {
                FaultId = closedFault.Id,
                IsSuppressed = true,
                SuppressedByFaultId = closedFault.Id,
                SuppressionReason = "silence_window",
            };
            await signalRepository.InsertAsync(finalSignal, cancellationToken);
            return new FaultGroupingOutcome(closedFault, Job: null, IsNewFault: false, IsNewJob: false, IsSuppressed: true);
        }
        return await CreateNewFaultAsync(draftSignal, closedFault?.Id, configuration, now, cancellationToken);
    }

    private async Task<FaultGroupingOutcome> AttachToOpenFaultAsync(
        Signal draftSignal,
        Fault openFault,
        TriageConfiguration configuration,
        CancellationToken cancellationToken)
    {
        return await intakeUnitOfWork.ExecuteAsync(
            async currentCancellationToken =>
            {
                var currentFault = await LockOpenFaultAsync(openFault.Id, currentCancellationToken);
                var finalSignal = draftSignal with { FaultId = currentFault.Id };
                await signalRepository.InsertAsync(finalSignal, currentCancellationToken);
                await neighborSetRefresher.RefreshAsync(currentFault, finalSignal, configuration, currentCancellationToken);
                var recurrenceAttachment = await recurrenceTracker.TrackAttachmentAsync(currentFault, finalSignal, triageJobRepository,
                    groundedFactsAssembler, configuration.FaultGrouping, currentCancellationToken);
                await recurrenceEscalationScheduler.ScheduleIfEscalatedAsync(recurrenceAttachment, currentFault, currentCancellationToken);
                return new FaultGroupingOutcome(currentFault, Job: null, IsNewFault: false, IsNewJob: false, IsSuppressed: false);
            },
            cancellationToken);
    }

    private async Task<Fault> LockOpenFaultAsync(Guid faultId, CancellationToken cancellationToken) =>
        (await faultRepository.FindByIdForUpdateAsync(faultId, cancellationToken)) is { Status: FaultStatus.Queued or FaultStatus.Analyzing } currentFault
            ? currentFault : throw new FaultGroupingStateChangedException(faultId);
    private async Task<FaultGroupingOutcome> CreateNewFaultAsync(
        Signal draftSignal,
        Guid? recurrenceOfFaultId,
        TriageConfiguration configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return await intakeUnitOfWork.ExecuteAsync(
            currentCancellationToken => CreateNewFaultCoreAsync(
                draftSignal,
                recurrenceOfFaultId,
                configuration,
                now,
                currentCancellationToken),
            cancellationToken);
    }

    private async Task<FaultGroupingOutcome> CreateNewFaultCoreAsync(
        Signal draftSignal,
        Guid? recurrenceOfFaultId,
        TriageConfiguration configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var signalWithoutFault = draftSignal with { FaultId = null };
        await signalRepository.InsertAsync(signalWithoutFault, cancellationToken);

        var candidateFault = new Fault(
            Id: Guid.NewGuid(),
            TriggerSignalId: draftSignal.Id,
            TenantId: draftSignal.TenantId,
            Status: FaultStatus.Queued,
            Fingerprint: draftSignal.Fingerprint!,
            FingerprintVersion: draftSignal.FingerprintVersion!.Value,
            FingerprintStrength: draftSignal.FingerprintStrength,
            CanGroup: draftSignal.CanGroup,
            ServiceName: draftSignal.ServiceName,
            Environment: draftSignal.Environment,
            Severity: draftSignal.Severity,
            CorrelationId: draftSignal.ExternalId,
            CreatedAtUtc: now,
            CompletedAtUtc: null,
            RecurrenceOf: recurrenceOfFaultId)
        {
            GroupingRuleId = draftSignal.GroupingRuleId,
            GroupingRuleVersion = draftSignal.GroupingRuleVersion
        };

        var insertedFault = await faultRepository.TryInsertAsync(candidateFault, cancellationToken);
        if (insertedFault is null)
        {
            var winningFault = await AttachToRaceWinnerAsync(draftSignal, configuration, cancellationToken);
            return new FaultGroupingOutcome(winningFault, Job: null, IsNewFault: false, IsNewJob: false, IsSuppressed: false);
        }

        var fault = insertedFault;
        await signalRepository.AttachToFaultAsync(draftSignal.Id, fault.Id, cancellationToken);
        var job = await triageJobRepository.InsertPendingAsync(fault.Id, configuration.ConfigHash, cancellationToken);
        var finalSignal = signalWithoutFault with { FaultId = fault.Id };
        var recurrenceState = await recurrenceTracker.TrackAsync(fault, finalSignal, job, configuration.FaultGrouping, cancellationToken);

        var neighborCount = draftSignal.CanGroup
            ? await FaultGroupingMetrics.CountNeighborsAsync(signalRepository, draftSignal, configuration.FaultGrouping, cancellationToken)
            : 0;
        var isMassIssue = FaultGroupingMetrics.DetermineIsMassIssue(draftSignal, neighborCount, configuration.FaultGrouping);

        await groundedFactsAssembler.AssembleAsync(job, finalSignal, fault, neighborCount, isMassIssue, configuration.FaultGrouping, cancellationToken, recurrenceState);
        await recurrenceEscalationScheduler.ScheduleIfEscalatedAsync(
            recurrenceState is null ? null : new RecurrenceAttachmentResult(job, recurrenceState),
            fault,
            cancellationToken);

        return new FaultGroupingOutcome(fault, job, IsNewFault: true, IsNewJob: true, IsSuppressed: false);
    }

    private async Task<Fault> AttachToRaceWinnerAsync(
        Signal draftSignal,
        TriageConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var winningFault = await faultRepository.FindOpenFaultAsync(
            draftSignal.TenantId, draftSignal.ServiceName, draftSignal.Environment,
            draftSignal.Fingerprint!, draftSignal.FingerprintVersion!.Value,
            draftSignal.GroupingRuleId, draftSignal.GroupingRuleVersion, cancellationToken)
            ?? throw new InvariantViolationException("Lost the fault-creation race but no open fault was found afterward.");
        winningFault = await LockOpenFaultAsync(winningFault.Id, cancellationToken);
        await signalRepository.AttachToFaultAsync(draftSignal.Id, winningFault.Id, cancellationToken);
        var finalSignal = draftSignal with { FaultId = winningFault.Id };
        await neighborSetRefresher.RefreshAsync(winningFault, finalSignal, configuration, cancellationToken);
        var recurrenceAttachment = await recurrenceTracker.TrackAttachmentAsync(
            winningFault, finalSignal, triageJobRepository, groundedFactsAssembler, configuration.FaultGrouping, cancellationToken);
        await recurrenceEscalationScheduler.ScheduleIfEscalatedAsync(recurrenceAttachment, winningFault, cancellationToken);
        return winningFault;
    }
}
