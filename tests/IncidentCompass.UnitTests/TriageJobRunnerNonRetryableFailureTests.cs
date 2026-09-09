using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Budget exhaustion and governance denial are permanent for a triage job: the same configuration
/// snapshot would exhaust or deny the replay identically. The runner must dead-letter those attempts
/// under their own error code instead of spending the retry budget, while provider outages and
/// ordinary faults keep their existing retry behavior.
/// </summary>
public sealed class TriageJobRunnerNonRetryableFailureTests
{
    public static TheoryData<string, string> ExhaustionCases => new()
    {
        { TriageBudgetExhaustedException.MaxTokensReachedCode, "The triage attempt token budget was reached before the next model call." },
        { TriageBudgetExhaustedException.WallClockReachedDuringCallCode, "The triage attempt exceeded MaxWallClockSeconds during a model call." },
        { TriageBudgetExhaustedException.WallClockReachedBeforeCallCode, "The triage attempt wall-clock budget was reached before the next model call." },
        { TriageBudgetExhaustedException.ContextWindowExceededCode, "The triage prompt exceeds the configured context window." },
        { TriageBudgetExhaustedException.OrchestratorTurnLimitReachedCode, "Orchestrator exceeded the bounded investigation turn limit before publish_report." },
        { TriageBudgetExhaustedException.OrchestratorRepromptLimitReachedCode, "publish_report remained invalid after bounded reprompts." },
        { TriageBudgetExhaustedException.MaxWorkersReachedCode, "The triage attempt worker budget was reached before delegation." },
        { TriageBudgetExhaustedException.WorkerTurnLimitReachedCode, "Worker exceeded the bounded tool/reprompt turn limit." }
    };

    [Theory]
    [MemberData(nameof(ExhaustionCases))]
    public async Task ProcessClaimedAsync_BudgetExhaustionDeadLettersOnceWithItsOwnErrorCode(string errorCode, string message)
    {
        var recorder = new RecordingRuntimeRepository();
        var runner = CreateRunner(recorder, new ThrowingProcessor(new TriageBudgetExhaustedException(errorCode, message)));

        await ProcessAsync(runner, attempt: 1, maxAttempts: 5);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.DeadLettered, failure.Status);
        Assert.Equal(errorCode, failure.ErrorCode);
        // last_error_message is a bounded classification, not the raw exception text (which for a
        // provider-originated failure could be an arbitrary upstream HTTP body); it is derived from
        // the same error code stored in last_error_code plus the exception type name, so this
        // assertion intentionally no longer echoes the exception's own message.
        Assert.Equal($"{errorCode}: {nameof(TriageBudgetExhaustedException)}.", failure.ErrorMessage);
        Assert.Null(failure.NextAttemptAtUtc);
        Assert.Equal(TriageJobRetryBudgetDisposition.ConsumeAttempt, failure.RetryBudgetDisposition);
    }

    [Fact]
    public async Task ProcessClaimedAsync_GovernanceDenialDeadLettersWithItsOwnErrorCode()
    {
        var recorder = new RecordingRuntimeRepository();
        var runner = CreateRunner(
            recorder,
            new ThrowingProcessor(new TriageGovernanceDeniedException(
                TriageGovernanceDeniedException.WorkerToolDeniedCode,
                "Worker tool call denied: rate_cap exceeded.")));

        await ProcessAsync(runner, attempt: 1, maxAttempts: 5);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.DeadLettered, failure.Status);
        Assert.Equal("triage_governance_worker_tool_denied", failure.ErrorCode);
        Assert.Null(failure.NextAttemptAtUtc);
    }

    [Fact]
    public async Task ProcessClaimedAsync_GovernanceValidationFailureDeadLettersWithItsOwnErrorCode()
    {
        var recorder = new RecordingRuntimeRepository();
        var runner = CreateRunner(
            recorder,
            new ThrowingProcessor(new TriageGovernanceDeniedException(
                TriageGovernanceDeniedException.WorkerToolValidationFailedCode,
                "Worker tool call validation failed after policy approval.")));

        await ProcessAsync(runner, attempt: 1, maxAttempts: 5);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.DeadLettered, failure.Status);
        Assert.Equal("triage_governance_worker_tool_validation_failed", failure.ErrorCode);
        Assert.Null(failure.NextAttemptAtUtc);
    }

    [Fact]
    public async Task ProcessClaimedAsync_InvalidWorkerOutputDeadLettersImmediatelyWithFixedReason()
    {
        const string modelControlledDiagnostic = "MODEL_OUTPUT_MUST_NOT_BE_STORED";
        var recorder = new RecordingRuntimeRepository();
        var validationFailure = new WorkerOutputValidationException(
            ["analysis worker output is not valid JSON: " + modelControlledDiagnostic],
            violationsTruncated: false);
        var runner = CreateRunner(
            recorder,
            new ThrowingProcessor(new WorkerOutputInvalidException(validationFailure)));

        await ProcessAsync(runner, attempt: 1, maxAttempts: 5);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.DeadLettered, failure.Status);
        Assert.Equal(WorkerOutputInvalidException.ErrorCode, failure.ErrorCode);
        Assert.Equal(WorkerOutputInvalidException.StoredReason, failure.ErrorMessage);
        Assert.DoesNotContain(modelControlledDiagnostic, failure.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(failure.NextAttemptAtUtc);
        Assert.Equal(TriageJobRetryBudgetDisposition.ConsumeAttempt, failure.RetryBudgetDisposition);
    }

    [Fact]
    public async Task ProcessClaimedAsync_ExhaustionWrappedByAnUntypedFailureStillDeadLetters()
    {
        var recorder = new RecordingRuntimeRepository();
        var inner = new TriageBudgetExhaustedException(
            TriageBudgetExhaustedException.MaxTokensReachedCode,
            "The triage attempt token budget was reached before the next model call.");
        var runner = CreateRunner(
            recorder,
            new ThrowingProcessor(new InvalidOperationException("Untyped wrapper.", inner)));

        await ProcessAsync(runner, attempt: 1, maxAttempts: 5);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.DeadLettered, failure.Status);
        Assert.Equal(TriageBudgetExhaustedException.MaxTokensReachedCode, failure.ErrorCode);
    }

    /// <summary>
    /// A spent orchestrator reprompt allowance carries the validation failure it could not correct.
    /// The disposition must come from the bounded limit that was actually reached, not from the
    /// carried cause, and the attempt must dead-letter with attempts still left on the job.
    /// </summary>
    [Fact]
    public async Task ProcessClaimedAsync_RepromptLimitDeadLettersUnderItsOwnCodeWithAttemptsRemaining()
    {
        var recorder = new RecordingRuntimeRepository();
        var runner = CreateRunner(
            recorder,
            new ThrowingProcessor(new TriageBudgetExhaustedException(
                TriageBudgetExhaustedException.OrchestratorRepromptLimitReachedCode,
                "publish_report remained invalid after bounded reprompts.",
                new TriageReportValidationException("report validation diagnostic"))));

        await ProcessAsync(runner, attempt: 1, maxAttempts: 5);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.DeadLettered, failure.Status);
        Assert.Equal(
            TriageBudgetExhaustedException.OrchestratorRepromptLimitReachedCode,
            failure.ErrorCode);
        Assert.Equal(
            $"{TriageBudgetExhaustedException.OrchestratorRepromptLimitReachedCode}: {nameof(TriageBudgetExhaustedException)}.",
            failure.ErrorMessage);
        // No next attempt time and no second recorded failure: the retry budget is not spent on a
        // limit that the same configuration snapshot would reach again.
        Assert.Null(failure.NextAttemptAtUtc);
        Assert.NotEqual("triage_job_attempt_failed", failure.ErrorCode);
        Assert.Equal(TriageJobRetryBudgetDisposition.ConsumeAttempt, failure.RetryBudgetDisposition);
    }

    [Fact]
    public async Task ProcessClaimedAsync_ExhaustionDoesNotRecordProviderFailureOrBackpressure()
    {
        var recorder = new RecordingRuntimeRepository();
        var tracker = new CountingProviderOutageTracker();
        var runner = CreateRunner(
            recorder,
            new ThrowingProcessor(new TriageBudgetExhaustedException(
                TriageBudgetExhaustedException.MaxTokensReachedCode,
                "The triage attempt token budget was reached before the next model call.")),
            tracker);

        await ProcessAsync(runner, attempt: 1, maxAttempts: 5);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.DeadLettered, failure.Status);
        Assert.Equal(TriageBudgetExhaustedException.MaxTokensReachedCode, failure.ErrorCode);
        Assert.Equal(0, tracker.FailureCount);
        Assert.False(tracker.IsBackpressured);
        Assert.NotEqual("provider_unavailable", failure.ErrorCode);
        Assert.NotEqual(TriageJobRetryBudgetDisposition.DoNotConsumeAttempt, failure.RetryBudgetDisposition);
    }

    [Fact]
    public async Task ProcessClaimedAsync_GovernanceDenialDoesNotRecordProviderFailureOrBackpressure()
    {
        var recorder = new RecordingRuntimeRepository();
        var tracker = new CountingProviderOutageTracker();
        var runner = CreateRunner(
            recorder,
            new ThrowingProcessor(new TriageGovernanceDeniedException(
                TriageGovernanceDeniedException.WorkerToolDeniedCode,
                "Worker tool call denied: tool_not_registered.")),
            tracker);

        await ProcessAsync(runner, attempt: 1, maxAttempts: 5);

        Assert.Equal(0, tracker.FailureCount);
        Assert.False(tracker.IsBackpressured);
    }

    [Fact]
    public async Task ProcessClaimedAsync_ProviderOutageStillRetriesWithoutConsumingTheAttempt()
    {
        var now = DateTimeOffset.UtcNow;
        var recorder = new RecordingRuntimeRepository();
        var tracker = new ProviderOutageTracker(
            Options.Create(new ProviderResilienceOptions { FailureThreshold = 1, BackpressureSeconds = 60 }),
            new FixedTimeProvider(now));
        var runner = CreateRunner(
            recorder,
            new ThrowingProcessor(new AiModelException(
                "test-provider",
                "Service unavailable.",
                failureKind: ProviderFailureKind.Unavailable)),
            tracker,
            new FixedTimeProvider(now));

        await ProcessAsync(runner, attempt: 1, maxAttempts: 1);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.RetryPending, failure.Status);
        Assert.Equal("provider_unavailable", failure.ErrorCode);
        Assert.Equal(TriageJobRetryBudgetDisposition.DoNotConsumeAttempt, failure.RetryBudgetDisposition);
        Assert.Equal(now.AddSeconds(60), failure.NextAttemptAtUtc);
        Assert.True(tracker.IsBackpressured);
    }

    [Fact]
    public async Task ProcessClaimedAsync_NonRetryableFailureInsideProviderWrapperTakesPrecedence()
    {
        var now = DateTimeOffset.UtcNow;
        var recorder = new RecordingRuntimeRepository();
        var exhaustion = new TriageBudgetExhaustedException(
            TriageBudgetExhaustedException.MaxTokensReachedCode,
            "The triage attempt token budget was reached before the next model call.");
        var runner = CreateRunner(
            recorder,
            new ThrowingProcessor(new InvalidOperationException(
                "Outage surfaced alongside exhaustion.",
                new AiModelException(
                    "test-provider",
                    "Nonsensical provider wrapper.",
                    innerException: exhaustion,
                    failureKind: ProviderFailureKind.Unavailable))),
            timeProvider: new FixedTimeProvider(now));

        await ProcessAsync(runner, attempt: 1, maxAttempts: 5);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.DeadLettered, failure.Status);
        Assert.Equal(TriageBudgetExhaustedException.MaxTokensReachedCode, failure.ErrorCode);
        Assert.Equal(TriageJobRetryBudgetDisposition.ConsumeAttempt, failure.RetryBudgetDisposition);
    }

    [Fact]
    public async Task ProcessClaimedAsync_OrdinaryFailureStillRetriesUntilMaxAttempts()
    {
        var now = DateTimeOffset.UtcNow;
        var recorder = new RecordingRuntimeRepository();
        var runner = CreateRunner(
            recorder,
            new ThrowingProcessor(new InvalidOperationException("Transient investigation fault.")),
            timeProvider: new FixedTimeProvider(now));

        await ProcessAsync(runner, attempt: 1, maxAttempts: 3);
        await ProcessAsync(runner, attempt: 2, maxAttempts: 3);
        await ProcessAsync(runner, attempt: 3, maxAttempts: 3);

        Assert.Equal(3, recorder.Failures.Count);
        Assert.All(recorder.Failures, failure => Assert.Equal("triage_job_attempt_failed", failure.ErrorCode));
        Assert.Equal(TriageJobStatus.RetryPending, recorder.Failures[0].Status);
        Assert.Equal(now.AddSeconds(5), recorder.Failures[0].NextAttemptAtUtc);
        Assert.Equal(TriageJobStatus.RetryPending, recorder.Failures[1].Status);
        Assert.Equal(TriageJobStatus.DeadLettered, recorder.Failures[2].Status);
        Assert.Null(recorder.Failures[2].NextAttemptAtUtc);
    }

    private static Task ProcessAsync(TriageJobRunner runner, int attempt, int maxAttempts) =>
        runner.ProcessClaimedAsync(
            CreateJob(attempt),
            "worker-test",
            new TriageJobProcessingSettings(maxAttempts, TimeSpan.FromSeconds(5)),
            TestContext.Current.CancellationToken);

    private static TriageJobRunner CreateRunner(
        ITriageJobRuntimeRepository runtimeRepository,
        IClaimedTriageJobProcessor processor,
        IProviderOutageTracker? providerOutageTracker = null,
        TimeProvider? timeProvider = null) =>
        new(
            runtimeRepository,
            new StaticConfigurationRepository(),
            processor,
            timeProvider ?? TimeProvider.System,
            providerOutageTracker);

    private static TriageJob CreateJob(int attempt)
    {
        var now = DateTimeOffset.UtcNow;
        return new TriageJob(
            Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Processing, attempt, "worker-test",
            now.AddMinutes(1), null, null, null, "config-hash", now, now);
    }

    private sealed class StaticConfigurationRepository : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(TestTriageConfiguration.Create());

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
            Task.FromResult(TestTriageConfiguration.Create());
    }

    private sealed class ThrowingProcessor(Exception exception) : IClaimedTriageJobProcessor
    {
        public Task ProcessAsync(
            TriageJob job,
            TriageConfiguration configuration,
            string workerId,
            CancellationToken cancellationToken) => Task.FromException(exception);
    }

    private sealed class RecordingRuntimeRepository : ITriageJobRuntimeRepository
    {
        public List<TriageJobAttemptFailure> Failures { get; } = [];

        public Task<TriageJob?> ClaimNextAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
            Task.FromResult<TriageJob?>(null);

        public Task<bool> RenewLeaseAsync(TriageJob job, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task RecordAttemptFailureAsync(TriageJob job, string workerId, TriageJobAttemptFailure failure, CancellationToken cancellationToken)
        {
            Failures.Add(failure);
            return Task.CompletedTask;
        }
    }

    private sealed class CountingProviderOutageTracker : IProviderOutageTracker
    {
        public int FailureCount { get; private set; }

        public bool IsBackpressured => FailureCount > 0;

        public TimeSpan RetryDelay => TimeSpan.FromSeconds(60);

        public void RecordProviderFailure() => FailureCount++;

        public void RecordProviderSuccess() => FailureCount = 0;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
