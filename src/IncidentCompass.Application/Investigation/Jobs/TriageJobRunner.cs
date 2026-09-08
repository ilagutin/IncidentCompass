using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.Observability;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Core.Text;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Application.Investigation.Jobs;

internal sealed partial class TriageJobRunner(
    ITriageJobRuntimeRepository runtimeRepository,
    ITriageConfigurationRepository configurationRepository,
    IClaimedTriageJobProcessor processor,
    TimeProvider timeProvider,
    IProviderOutageTracker? providerOutageTracker = null,
    IRuntimeTelemetry? telemetry = null,
    ILogger<TriageJobRunner>? logger = null) : ITriageJobRunner
{
    private const int MaxStoredErrorMessageLength = 1000;

    private readonly ILogger logger = logger ?? NullLogger<TriageJobRunner>.Instance;

    public Task<TriageJob?> ClaimNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        return runtimeRepository.ClaimNextAsync(workerId, leaseDuration, cancellationToken);
    }

    public Task<bool> RenewLeaseAsync(
        TriageJob job,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken) =>
        runtimeRepository.RenewLeaseAsync(job, workerId, leaseDuration, cancellationToken);
    public async Task ProcessClaimedAsync(
        TriageJob job,
        string workerId,
        TriageJobProcessingSettings settings,
        CancellationToken cancellationToken)
    {
        var configurationLoaded = false;
        using var attemptTelemetry = telemetry?.StartJobAttempt();
        var maxAttempts = Math.Max(1, settings.MaxAttempts);
        if (job.Attempt > maxAttempts)
        {
            telemetry?.RecordJobAttempt(RuntimeTelemetryOutcome.Failed);
            var exhaustedFailure = new TriageJobAttemptFailure(
                TriageJobStatus.DeadLettered,
                "triage_job_attempt_limit_exhausted",
                "triage_job_attempt_limit_exhausted: attempt guard.",
                NextAttemptAtUtc: null);
            LogAttemptFailed(
                logger,
                job.Id,
                job.Attempt,
                exhaustedFailure.ErrorCode,
                "AttemptLimitGuard");
            LogAttemptDisposition(job, exhaustedFailure);
            await RecordAttemptFailureAsync(job, workerId, exhaustedFailure);
            return;
        }

        try
        {
            var configuration = await configurationRepository.GetByHashAsync(job.ConfigHash, cancellationToken);
            configurationLoaded = true;
            await processor.ProcessAsync(job, configuration, workerId, cancellationToken);
            telemetry?.RecordJobAttempt(RuntimeTelemetryOutcome.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry?.RecordJobAttempt(RuntimeTelemetryOutcome.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            var nonRetryableErrorCode = TriageNonRetryableFailureClassifier.TryGetErrorCode(exception);
            var providerFailureKind = nonRetryableErrorCode is null
                ? ProviderOutageExceptionClassifier.FindFailureKind(exception)
                : null;
            var providerOutage = providerFailureKind == ProviderFailureKind.Unavailable;
            if (providerOutage)
            {
                telemetry?.RecordJobAttempt(RuntimeTelemetryOutcome.ProviderUnavailable);
                providerOutageTracker?.RecordProviderFailure();
            }

            if (!providerOutage)
            {
                telemetry?.RecordJobAttempt(RuntimeTelemetryOutcome.Failed);
            }

            var failure = CreateFailure(
                job,
                settings,
                exception,
                configurationLoaded,
                nonRetryableErrorCode,
                providerFailureKind);

            // The original failure is logged before any durable write is attempted, so a secondary
            // persistence fault can never erase the trace of what actually failed.
            LogAttemptFailed(logger, job.Id, job.Attempt, failure.ErrorCode, exception.GetType().Name);
            LogAttemptDisposition(job, failure);

            await RecordAttemptFailureAsync(job, workerId, failure);
        }
    }

    private async Task RecordAttemptFailureAsync(
        TriageJob job,
        string workerId,
        TriageJobAttemptFailure failure)
    {
        try
        {
            await runtimeRepository.RecordAttemptFailureAsync(
                job,
                workerId,
                failure,
                CancellationToken.None);
        }
        catch (Exception persistenceException)
        {
            LogAttemptFailurePersistenceFailed(
                logger, job.Id, job.Attempt, persistenceException.GetType().Name);
            throw;
        }
    }

    private void LogAttemptDisposition(TriageJob job, TriageJobAttemptFailure failure)
    {
        if (failure.RetryBudgetDisposition == TriageJobRetryBudgetDisposition.DoNotConsumeAttempt)
        {
            LogAttemptDelayedForProviderOutage(logger, job.Id, job.Attempt, failure.NextAttemptAtUtc);
            return;
        }

        if (failure.Status == TriageJobStatus.DeadLettered)
        {
            LogAttemptDeadLettered(logger, job.Id, job.Attempt, failure.ErrorCode);
            return;
        }

        LogAttemptRetryScheduled(logger, job.Id, job.Attempt, failure.ErrorCode, failure.NextAttemptAtUtc);
    }

    private TriageJobAttemptFailure CreateFailure(
        TriageJob job,
        TriageJobProcessingSettings settings,
        Exception exception,
        bool configurationLoaded,
        string? nonRetryableErrorCode,
        ProviderFailureKind? providerFailureKind)
    {
        var accounting = FindModelCallAccounting(exception);
        if (nonRetryableErrorCode is not null)
        {
            return new TriageJobAttemptFailure(
                TriageJobStatus.DeadLettered,
                nonRetryableErrorCode,
                NormalizeMessage(nonRetryableErrorCode, exception),
                NextAttemptAtUtc: null,
                ModelCallAccounting: accounting);
        }

        if (providerFailureKind == ProviderFailureKind.Unavailable)
        {
            return new TriageJobAttemptFailure(
                TriageJobStatus.RetryPending,
                "provider_unavailable",
                "Triage delayed: provider unavailable.",
                timeProvider.GetUtcNow().Add(providerOutageTracker?.RetryDelay ?? settings.RetryDelay),
                TriageJobRetryBudgetDisposition.DoNotConsumeAttempt,
                accounting);
        }

        if (providerFailureKind is ProviderFailureKind.RejectedRequest or
            ProviderFailureKind.OutputLimitReached or
            ProviderFailureKind.AmbiguousInterruption)
        {
            var immediateFailureCode = GetProviderErrorCode(providerFailureKind.Value, exception);
            return new TriageJobAttemptFailure(
                TriageJobStatus.DeadLettered,
                immediateFailureCode,
                NormalizeMessage(immediateFailureCode, exception),
                NextAttemptAtUtc: null,
                ModelCallAccounting: accounting);
        }

        var maxAttempts = Math.Max(1, settings.MaxAttempts);
        var errorCode = providerFailureKind is { } failureKind
            ? GetProviderErrorCode(failureKind, exception)
            : configurationLoaded
                ? "triage_job_attempt_failed"
                : "config_snapshot_unavailable";
        if (job.Attempt >= maxAttempts)
        {
            return new TriageJobAttemptFailure(
                TriageJobStatus.DeadLettered,
                errorCode,
                NormalizeMessage(errorCode, exception),
                NextAttemptAtUtc: null,
                ModelCallAccounting: accounting);
        }

        return new TriageJobAttemptFailure(
            TriageJobStatus.RetryPending,
            errorCode,
            NormalizeMessage(errorCode, exception),
            timeProvider.GetUtcNow().Add(settings.RetryDelay),
            ModelCallAccounting: accounting);
    }

    private static string GetProviderErrorCode(ProviderFailureKind failureKind, Exception exception) =>
        failureKind switch
        {
            ProviderFailureKind.Unavailable => "provider_unavailable",
            ProviderFailureKind.RejectedRequest => "provider_request_rejected",
            ProviderFailureKind.GenerationTimeout => "provider_generation_timeout",
            ProviderFailureKind.OutputLimitReached => "provider_output_limit_reached",
            ProviderFailureKind.AmbiguousInterruption => "provider_dispatch_outcome_unknown",
            ProviderFailureKind.InvalidResponse =>
                ProviderOutageExceptionClassifier.FindSafeErrorCode(exception) ?? "provider_invalid_response",
            _ => ProviderOutageExceptionClassifier.FindSafeErrorCode(exception) ?? "provider_failure"
        };

    private static InvestigationModelCallAccounting? FindModelCallAccounting(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is InvestigationModelCallFailureException modelCallFailure)
            {
                return modelCallFailure.Accounting;
            }
        }

        return null;
    }

    // The stored message is a bounded, self-explanatory classification, not the raw exception
    // text: for a provider failure, exception.Message can be an arbitrary upstream HTTP body.
    // The error code is the same closed token already stored in the sibling last_error_code
    // column (see TriageNonRetryableFailureClassifier and the codes above), so a row read
    // directly from the database is explained by its message alone without a second lookup.
    private static string NormalizeMessage(string errorCode, Exception exception)
    {
        var classified = $"{errorCode}: {exception.GetType().Name}.";
        return TextTruncator.Truncate(classified, MaxStoredErrorMessageLength);
    }

    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Warning,
        Message = "Triage job {JobId} attempt {Attempt} failed with error code {ErrorCode} raised by {ExceptionType}.")]
    private static partial void LogAttemptFailed(
        ILogger logger,
        Guid jobId,
        int attempt,
        string errorCode,
        string exceptionType);

    [LoggerMessage(
        EventId = 3102,
        Level = LogLevel.Information,
        Message = "Triage job {JobId} attempt {Attempt} with error code {ErrorCode} is retry-pending until {NextAttemptAtUtc}.")]
    private static partial void LogAttemptRetryScheduled(
        ILogger logger,
        Guid jobId,
        int attempt,
        string errorCode,
        DateTimeOffset? nextAttemptAtUtc);

    [LoggerMessage(
        EventId = 3103,
        Level = LogLevel.Error,
        Message = "Triage job {JobId} attempt {Attempt} was dead-lettered with error code {ErrorCode} and will not be retried.")]
    private static partial void LogAttemptDeadLettered(
        ILogger logger,
        Guid jobId,
        int attempt,
        string errorCode);

    [LoggerMessage(
        EventId = 3104,
        Level = LogLevel.Warning,
        Message = "Triage job {JobId} attempt {Attempt} was delayed until {NextAttemptAtUtc} because the model provider is unavailable; the attempt budget was not consumed.")]
    private static partial void LogAttemptDelayedForProviderOutage(
        ILogger logger,
        Guid jobId,
        int attempt,
        DateTimeOffset? nextAttemptAtUtc);

    [LoggerMessage(
        EventId = 3105,
        Level = LogLevel.Error,
        Message = "Triage job {JobId} attempt {Attempt} could not record its attempt failure; the runtime repository raised {ExceptionType}.")]
    private static partial void LogAttemptFailurePersistenceFailed(
        ILogger logger,
        Guid jobId,
        int attempt,
        string exceptionType);
}
