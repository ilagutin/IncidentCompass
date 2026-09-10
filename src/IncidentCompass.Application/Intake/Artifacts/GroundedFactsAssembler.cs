using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.FaultGrouping;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Intake.Artifacts;

public sealed class GroundedFactsAssembler(
    ITriageArtifactRepository artifactRepository,
    IPriorReportSummaryProvider priorReportSummaryProvider,
    TimeProvider timeProvider)
{
    public async Task AssembleAsync(
        TriageJob job,
        Signal triggerSignal,
        Fault fault,
        int neighborCount,
        bool? isMassIssue,
        FaultGroupingSettings settings,
        CancellationToken cancellationToken,
        RecurrenceState? recurrenceState = null)
    {
        await InsertTriggerSignalArtifactAsync(job, triggerSignal, cancellationToken);
        await InsertNeighborSetArtifactAsync(job, fault, triggerSignal, neighborCount, isMassIssue, settings, cancellationToken);
        await InsertRecurrenceStateArtifactAsync(job, recurrenceState, cancellationToken);
        await InsertPriorReportArtifactIfRecurrenceAsync(job, fault, cancellationToken);
    }

    public async Task ReplaceNeighborSetAsync(
        TriageJob job,
        Fault fault,
        Signal triggerSignal,
        int neighborCount,
        bool? isMassIssue,
        FaultGroupingSettings settings,
        CancellationToken cancellationToken)
    {
        var artifact = CreateNeighborSetArtifact(job, fault, triggerSignal, neighborCount, isMassIssue, settings);
        await artifactRepository.ReplaceJobLevelAsync(artifact, cancellationToken);
    }

    private async Task InsertTriggerSignalArtifactAsync(TriageJob job, Signal triggerSignal, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["signalId"] = triggerSignal.Id.ToString(),
            ["source"] = triggerSignal.Source,
            ["serviceName"] = triggerSignal.ServiceName,
            ["environment"] = triggerSignal.Environment,
            ["severity"] = triggerSignal.Severity,
            ["errorType"] = triggerSignal.ErrorType,
            ["errorMessage"] = triggerSignal.ErrorMessage,
            ["summary"] = triggerSignal.Summary,
            ["observedAtUtc"] = triggerSignal.ObservedAtUtc.ToString("O"),
        };

        await InsertArtifactAsync(job.Id, attempt: null, ArtifactKind.TriggerSignal, $"signal:{triggerSignal.Id}", payload, cancellationToken);
    }

    private async Task InsertNeighborSetArtifactAsync(
        TriageJob job,
        Fault fault,
        Signal triggerSignal,
        int neighborCount,
        bool? isMassIssue,
        FaultGroupingSettings settings,
        CancellationToken cancellationToken)
    {
        var artifact = CreateNeighborSetArtifact(job, fault, triggerSignal, neighborCount, isMassIssue, settings);
        await artifactRepository.InsertAsync(artifact, cancellationToken);
    }

    private TriageArtifact CreateNeighborSetArtifact(
        TriageJob job,
        Fault fault,
        Signal triggerSignal,
        int neighborCount,
        bool? isMassIssue,
        FaultGroupingSettings settings)
    {
        var payload = new JsonObject
        {
            ["neighborCount"] = neighborCount,
            ["neighborCountApplies"] = triggerSignal.CanGroup,
            ["isMassIssue"] = isMassIssue,
            ["lookbackMinutes"] = settings.LookbackMinutes,
            ["minNeighborCountThreshold"] = settings.MassIssue.MinNeighborCount,
            ["minFingerprintStrengthThreshold"] = settings.MassIssue.MinFingerprintStrength,
            ["fingerprintStrength"] = triggerSignal.FingerprintStrength.ToString(),
            ["groupingRuleId"] = triggerSignal.GroupingRuleId,
            ["groupingRuleVersion"] = triggerSignal.GroupingRuleVersion,
            ["suppressionRuleId"] = triggerSignal.SuppressionRuleId,
            ["effectiveSuppressionWindowMinutes"] = triggerSignal.EffectiveSuppressionWindowMinutes,
        };

        return CreateArtifact(job.Id, attempt: null, ArtifactKind.NeighborSet, $"fault:{fault.Id}", payload);
    }

    public Task ReplaceRecurrenceStateAsync(TriageJob job, RecurrenceState recurrenceState, CancellationToken cancellationToken) =>
        artifactRepository.ReplaceJobLevelAsync(CreateRecurrenceStateArtifact(job, recurrenceState), cancellationToken);

    private async Task InsertRecurrenceStateArtifactAsync(
        TriageJob job,
        RecurrenceState? recurrenceState,
        CancellationToken cancellationToken)
    {
        if (recurrenceState is null)
        {
            return;
        }

        var artifact = CreateRecurrenceStateArtifact(job, recurrenceState);
        await artifactRepository.InsertAsync(artifact, cancellationToken);
    }

    private TriageArtifact CreateRecurrenceStateArtifact(TriageJob job, RecurrenceState recurrenceState)
    {
        var payload = new JsonObject
        {
            ["recurrenceCount"] = recurrenceState.Count,
            ["firstRecurrenceAtUtc"] = recurrenceState.FirstOccurredAtUtc.ToString("O"),
            ["lastRecurrenceAtUtc"] = recurrenceState.LastOccurredAtUtc.ToString("O"),
            ["escalationIntentCreated"] = recurrenceState.EscalationIntentCreatedFor(job.Id),
            ["escalationIntentJobId"] = recurrenceState.EscalationIntentJobId?.ToString(),
        };

        return CreateArtifact(job.Id, attempt: null, ArtifactKind.RecurrenceState, $"job:{job.Id}", payload);
    }

    private async Task InsertPriorReportArtifactIfRecurrenceAsync(TriageJob job, Fault fault, CancellationToken cancellationToken)
    {
        if (fault.RecurrenceOf is not Guid recurrenceOfFaultId)
        {
            return;
        }

        var prior = await priorReportSummaryProvider.FindLatestAsync(recurrenceOfFaultId, cancellationToken);
        if (prior is null)
        {
            return;
        }

        var payload = new JsonObject
        {
            ["summary"] = prior.Summary,
            ["limitations"] = new JsonArray(prior.Limitations.Select(limitation => JsonValue.Create(limitation) as JsonNode).ToArray()),
            ["trust"] = "untrusted-prior-hypothesis",
        };

        var domainRef = prior.ReportId is Guid reportId ? $"report:{reportId}" : $"fault:{recurrenceOfFaultId}";
        await InsertArtifactAsync(job.Id, attempt: null, ArtifactKind.PriorReport, domainRef, payload, cancellationToken);
    }

    private async Task InsertArtifactAsync(
        Guid jobId,
        int? attempt,
        ArtifactKind kind,
        string domainRef,
        JsonObject payload,
        CancellationToken cancellationToken)
    {
        var artifact = CreateArtifact(jobId, attempt, kind, domainRef, payload);
        await artifactRepository.InsertAsync(artifact, cancellationToken);
    }

    private TriageArtifact CreateArtifact(
        Guid jobId,
        int? attempt,
        ArtifactKind kind,
        string domainRef,
        JsonObject payload)
    {
        var canonicalPayload = CanonicalJsonSerializer.Canonicalize(payload);
        using var payloadDocument = JsonDocument.Parse(payload.ToJsonString());
        return new TriageArtifact(
            Id: attempt is null ? CreateJobLevelArtifactId(jobId, kind) : Guid.NewGuid(),
            JobId: jobId,
            Attempt: attempt,
            Kind: kind,
            DomainRef: domainRef,
            RedactedPayload: payloadDocument.RootElement.Clone(),
            ContentHash: CanonicalJsonSerializer.ComputeSha256Hex(canonicalPayload),
            CreatedAtUtc: timeProvider.GetUtcNow());
    }

    private static Guid CreateJobLevelArtifactId(Guid jobId, ArtifactKind kind)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(jobId.ToString("N") + ":" + kind));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }
}
