using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Core.Tenancy;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.FaultGrouping;
using IncidentCompass.Application.Intake.Fingerprinting;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Intake.Redaction;
using IncidentCompass.Domain.Exceptions;
using IncidentCompass.Domain.Incidents;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.Application.Intake.IngestSignal;

public sealed class IngestSignalCommandHandler(
    ITriageConfigurationRepository configurationRepository,
    SignalNormalizerRegistry normalizerRegistry,
    UserIdentifierPseudonymizer pseudonymizer,
    FaultGroupingCoordinator faultGroupingCoordinator,
    ISignalRepository signalRepository,
    TimeProvider timeProvider,
    IIncidentTenantContext incidentTenantContext,
    ILogger<IngestSignalCommandHandler> logger) : IRequestHandler<IngestSignalCommand, IngestSignalResponse>
{
    public async Task<IngestSignalResponse> HandleAsync(IngestSignalCommand command, CancellationToken cancellationToken)
    {
        var configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
        var receivedAtUtc = timeProvider.GetUtcNow();
        var normalizer = normalizerRegistry.Resolve(command.SourceKind);
        var normalized = normalizer.Normalize(command, receivedAtUtc);
        var pseudonymized = pseudonymizer.Protect(normalized, configuration.Redaction);
        var redacted = SecretRedactor.Redact(
            pseudonymized,
            configuration.Redaction,
            pseudonymizer.IsCanonicalPseudonym,
            logger);
        var fingerprint = FingerprintCalculator.Compute(redacted, configuration.FaultGrouping);
        var tenantId = await incidentTenantContext.GetTenantIdAsync(cancellationToken);
        var draftSignal = BuildSignal(
            redacted,
            fingerprint,
            tenantId,
            receivedAtUtc);

        ExistingSignalDelivery? existingDelivery = null;
        if (draftSignal.DeliveryKey is { } existingDeliveryKey)
        {
            existingDelivery = await signalRepository.FindDeliveryAsync(
                draftSignal.TenantId,
                draftSignal.Source,
                existingDeliveryKey,
                cancellationToken);
        }

        if (existingDelivery is not null)
        {
            return FromExistingDelivery(existingDelivery);
        }
        try
        {
            var outcome = await faultGroupingCoordinator.ResolveAsync(draftSignal, configuration, cancellationToken);
            return new IngestSignalResponse(
                draftSignal.Id,
                outcome.Fault.Id,
                outcome.IsNewFault,
                outcome.IsNewJob,
                outcome.IsSuppressed,
                outcome.Job?.Id,
                outcome.Job?.ConfigHash);
        }
        catch (DuplicateSignalDeliveryException)
        {
            if (draftSignal.DeliveryKey is not { } raceDeliveryKey)
            {
                throw;
            }

            existingDelivery = await signalRepository.FindDeliveryAsync(
                draftSignal.TenantId,
                draftSignal.Source,
                raceDeliveryKey,
                cancellationToken)
                ?? throw new InvariantViolationException("A duplicate signal delivery was reported but no accepted signal was found.");
            return FromExistingDelivery(existingDelivery);
        }
    }

    private static IngestSignalResponse FromExistingDelivery(ExistingSignalDelivery delivery) =>
        new(
            delivery.SignalId,
            delivery.FaultId,
            IsNewFault: false,
            IsNewJob: false,
            delivery.IsSuppressed,
            delivery.JobId,
            delivery.ConfigHash);

    private static string? CreateDeliveryKey(NormalizedSignal signal)
    {
        if (!string.IsNullOrWhiteSpace(signal.ExternalId))
        {
            return "external:" + signal.ExternalId;
        }

        return !string.IsNullOrWhiteSpace(signal.TraceId) && !string.IsNullOrWhiteSpace(signal.SpanId)
            ? $"trace:{signal.TraceId}:span:{signal.SpanId}"
            : null;
    }

    private static Signal BuildSignal(
        NormalizedSignal redacted,
        FingerprintResult fingerprint,
        string tenantId,
        DateTimeOffset receivedAtUtc)
    {
        return new Signal(
            Id: Guid.NewGuid(),
            TenantId: tenantId,
            Source: redacted.Source,
            FaultId: null,
            Fingerprint: fingerprint.Value,
            FingerprintVersion: fingerprint.EffectiveRule.Version,
            FingerprintStrength: fingerprint.Strength,
            CanGroup: fingerprint.Strength == FingerprintStrength.Strong,
            ExternalId: redacted.ExternalId,
            IsSuppressed: false,
            SuppressedByFaultId: null,
            SuppressionReason: null,
            TraceId: redacted.TraceId,
            SpanId: redacted.SpanId,
            ParentSpanId: redacted.ParentSpanId,
            ServiceName: redacted.ServiceName,
            Environment: redacted.Environment,
            OperationName: redacted.OperationName,
            Severity: redacted.Severity,
            ErrorType: redacted.ErrorType,
            ErrorMessage: redacted.ErrorMessage,
            Summary: redacted.Summary,
            Description: redacted.Description,
            HttpMethod: redacted.HttpMethod,
            HttpRoute: redacted.HttpRoute,
            HttpStatusCode: redacted.HttpStatusCode,
            DurationMs: redacted.DurationMs,
            Attributes: CanonicalJsonSerializer.ToElement(redacted.Attributes),
            Body: CanonicalJsonSerializer.ToElement(redacted.Body),
            ObservedAtUtc: redacted.ObservedAtUtc,
            ReceivedAtUtc: receivedAtUtc,
            DeliveryKey: CreateDeliveryKey(redacted))
        {
            GroupingRuleId = fingerprint.EffectiveRule.Id,
            GroupingRuleVersion = fingerprint.EffectiveRule.Version
        };
    }
}
