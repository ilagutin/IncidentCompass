using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

public sealed class TriageJobRunnerProviderOutageTests
{
    public static TheoryData<ProviderFailureKind, bool> ClassificationCases => new()
    {
        { ProviderFailureKind.Unavailable, true },
        { ProviderFailureKind.RejectedRequest, false },
        { ProviderFailureKind.GenerationTimeout, false },
        { ProviderFailureKind.OutputLimitReached, false },
        { ProviderFailureKind.AmbiguousInterruption, false },
        { ProviderFailureKind.TransportFailure, false },
        { ProviderFailureKind.InvalidResponse, false },
        { ProviderFailureKind.Unknown, false }
    };

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void IsProviderOutage_UsesFailureKindAcrossNestedChains(
        ProviderFailureKind failureKind,
        bool expected)
    {
        var providerFailure = new AiModelException(
            "test-provider",
            "Provider failure.",
            failureKind: failureKind);

        Assert.Equal(expected, ProviderOutageExceptionClassifier.IsProviderOutage(providerFailure));
        Assert.Equal(
            expected,
            ProviderOutageExceptionClassifier.IsProviderOutage(
                new InvalidOperationException("Outer wrapper.", providerFailure)));
    }

    [Fact]
    public async Task ProcessClaimedAsync_ProviderOutageDelaysWithoutBurningAttemptLimit()
    {
        var now = DateTimeOffset.UtcNow;
        var recorder = new RecordingRuntimeRepository();
        var tracker = new ProviderOutageTracker(
            Options.Create(new ProviderResilienceOptions { FailureThreshold = 1, BackpressureSeconds = 60 }),
            new FixedTimeProvider(now));
        var runner = new TriageJobRunner(
            recorder,
            new StaticConfigurationRepository(),
            new ProviderFailingProcessor(new AiModelException(
                "test-provider",
                "Service unavailable.",
                failureKind: ProviderFailureKind.Unavailable)),
            new FixedTimeProvider(now),
            tracker);
        var job = new TriageJob(
            Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Processing, Attempt: 1, "worker", now.AddMinutes(1),
            null, null, null, "config", now, now);

        await runner.ProcessClaimedAsync(
            job,
            "worker",
            new TriageJobProcessingSettings(MaxAttempts: 1, RetryDelay: TimeSpan.FromSeconds(1)),
            CancellationToken.None);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.RetryPending, failure.Status);
        Assert.Equal("provider_unavailable", failure.ErrorCode);
        Assert.Equal(TriageJobRetryBudgetDisposition.DoNotConsumeAttempt, failure.RetryBudgetDisposition);
        Assert.Equal("Triage delayed: provider unavailable.", failure.ErrorMessage);
        Assert.Equal(now.AddSeconds(60), failure.NextAttemptAtUtc);
        Assert.True(tracker.IsBackpressured);
    }

    [Theory]
    [InlineData(ProviderFailureKind.RejectedRequest, 1, 3, TriageJobStatus.DeadLettered, "provider_request_rejected")]
    [InlineData(ProviderFailureKind.GenerationTimeout, 1, 3, TriageJobStatus.RetryPending, "provider_generation_timeout")]
    [InlineData(ProviderFailureKind.GenerationTimeout, 3, 3, TriageJobStatus.DeadLettered, "provider_generation_timeout")]
    [InlineData(ProviderFailureKind.OutputLimitReached, 1, 3, TriageJobStatus.DeadLettered, "provider_output_limit_reached")]
    [InlineData(ProviderFailureKind.AmbiguousInterruption, 1, 3, TriageJobStatus.DeadLettered, "provider_dispatch_outcome_unknown")]
    [InlineData(ProviderFailureKind.InvalidResponse, 1, 3, TriageJobStatus.RetryPending, "provider_invalid_response")]
    [InlineData(ProviderFailureKind.Unknown, 1, 3, TriageJobStatus.RetryPending, "provider_failure")]
    [InlineData(ProviderFailureKind.Unknown, 3, 3, TriageJobStatus.DeadLettered, "provider_failure")]
    public async Task ProcessClaimedAsync_ProviderFailureKindHasFiniteDisposition(
        ProviderFailureKind failureKind,
        int attempt,
        int maxAttempts,
        TriageJobStatus expectedStatus,
        string expectedCode)
    {
        var now = DateTimeOffset.UtcNow;
        var recorder = new RecordingRuntimeRepository();
        var processor = new ProviderFailingProcessor(new InvalidOperationException(
            "Nested provider failure.",
            new AiModelException(
                "test-provider",
                "Provider failure.",
                failureKind: failureKind)));
        var runner = new TriageJobRunner(
            recorder,
            new StaticConfigurationRepository(),
            processor,
            new FixedTimeProvider(now));

        await runner.ProcessClaimedAsync(
            CreateJob(now, attempt),
            "worker",
            new TriageJobProcessingSettings(maxAttempts, TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(expectedStatus, failure.Status);
        Assert.Equal(expectedCode, failure.ErrorCode);
        Assert.Equal(TriageJobRetryBudgetDisposition.ConsumeAttempt, failure.RetryBudgetDisposition);
    }

    [Theory]
    [InlineData(1, TriageJobStatus.RetryPending)]
    [InlineData(3, TriageJobStatus.DeadLettered)]
    public async Task ProcessClaimedAsync_EmbeddingTransportFailureConsumesFiniteAttemptBudget(
        int attempt,
        TriageJobStatus expectedStatus)
    {
        var now = DateTimeOffset.UtcNow;
        var recorder = new RecordingRuntimeRepository();
        var embeddingFailure = new EmbeddingClientException(
            "openai-compatible",
            "Embedding transport failed.",
            errorCode: "transport_error",
            failureKind: ProviderFailureKind.TransportFailure);
        var runner = new TriageJobRunner(
            recorder,
            new StaticConfigurationRepository(),
            new ProviderFailingProcessor(new InvalidOperationException(
                "Nested embedding transport failure.",
                embeddingFailure)),
            new FixedTimeProvider(now));

        await runner.ProcessClaimedAsync(
            CreateJob(now, attempt),
            "worker",
            new TriageJobProcessingSettings(MaxAttempts: 3, RetryDelay: TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(expectedStatus, failure.Status);
        Assert.Equal("transport_error", failure.ErrorCode);
        Assert.Equal(TriageJobRetryBudgetDisposition.ConsumeAttempt, failure.RetryBudgetDisposition);
        Assert.False(ProviderOutageExceptionClassifier.IsProviderOutage(embeddingFailure));
    }

    /// <summary>
    /// A model call that failed over and failed again arrives here as the fallback call's accounting
    /// wrapped over the primary's failure. The runner must read one failure kind out of that, the
    /// primary's, so the disposition table still describes what a job does; and it must persist the
    /// accounting still owed, which is the fallback call's, because the primary's was made durable
    /// before the second call was allowed to start.
    /// </summary>
    [Fact]
    public async Task ProcessClaimedAsync_FailedFailOverKeepsThePrimarysDispositionAndTheFallbacksAccounting()
    {
        var now = DateTimeOffset.UtcNow;
        var recorder = new RecordingRuntimeRepository();
        var primaryFailure = new InvestigationModelCallFailureException(
            CreateAccounting("report-chat", fallbackForRouteId: null, "provider_unavailable"),
            new AiModelException(
                "test-provider",
                "Service unavailable.",
                failureKind: ProviderFailureKind.Unavailable));
        var fallbackAccounting = CreateAccounting("backup-chat", "report-chat", "provider_request_rejected");
        var runner = new TriageJobRunner(
            recorder,
            new StaticConfigurationRepository(),
            new ProviderFailingProcessor(
                new InvestigationModelCallFailureException(fallbackAccounting, primaryFailure)),
            new FixedTimeProvider(now));

        await runner.ProcessClaimedAsync(
            CreateJob(now, attempt: 1),
            "worker",
            new TriageJobProcessingSettings(MaxAttempts: 3, RetryDelay: TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.RetryPending, failure.Status);
        Assert.Equal("provider_unavailable", failure.ErrorCode);
        Assert.Equal(TriageJobRetryBudgetDisposition.DoNotConsumeAttempt, failure.RetryBudgetDisposition);
        Assert.Same(fallbackAccounting, failure.ModelCallAccounting);
    }

    private static InvestigationModelCallAccounting CreateAccounting(
        string routeId,
        string? fallbackForRouteId,
        string errorCode)
    {
        var callId = Guid.NewGuid();
        return new InvestigationModelCallAccounting(
            callId,
            Role: null,
            new ModelCallLedgerMetadata(
                "orchestrator",
                routeId,
                "test-model",
                "test-provider",
                "unknown",
                InputTokens: null,
                OutputTokens: null,
                TotalTokens: null,
                DurationMs: 12,
                ProposedToolCallCount: 0,
                CallId: callId,
                Outcome: "failed",
                ErrorCode: errorCode,
                ReasoningTokens: null,
                FallbackForRouteId: fallbackForRouteId),
            ChargeTokens: null);
    }

    [Fact]
    public async Task ProcessClaimedAsync_UnknownFailurePreservesSafeProviderErrorCode()
    {
        var now = DateTimeOffset.UtcNow;
        var recorder = new RecordingRuntimeRepository();
        var runner = new TriageJobRunner(
            recorder,
            new StaticConfigurationRepository(),
            new ProviderFailingProcessor(new AiModelException(
                "test-provider",
                "Provider failure.",
                errorCode: "provider_empty_response")),
            new FixedTimeProvider(now));

        await runner.ProcessClaimedAsync(
            CreateJob(now, attempt: 1),
            "worker",
            new TriageJobProcessingSettings(MaxAttempts: 2, RetryDelay: TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal("provider_empty_response", failure.ErrorCode);
        Assert.Equal(TriageJobStatus.RetryPending, failure.Status);
    }

    [Fact]
    public async Task ProcessClaimedAsync_AttemptBeyondMaximumDeadLettersWithoutLoadingOrProcessing()
    {
        var now = DateTimeOffset.UtcNow;
        var recorder = new RecordingRuntimeRepository();
        var configuration = new StaticConfigurationRepository();
        var processor = new CountingProcessor();
        var runner = new TriageJobRunner(recorder, configuration, processor, new FixedTimeProvider(now));

        await runner.ProcessClaimedAsync(
            CreateJob(now, attempt: 4),
            "worker",
            new TriageJobProcessingSettings(MaxAttempts: 3, RetryDelay: TimeSpan.FromSeconds(5)),
            CancellationToken.None);

        var failure = Assert.Single(recorder.Failures);
        Assert.Equal(TriageJobStatus.DeadLettered, failure.Status);
        Assert.Equal("triage_job_attempt_limit_exhausted", failure.ErrorCode);
        Assert.Equal(0, configuration.ReadCount);
        Assert.Equal(0, processor.CallCount);
    }

    [Fact]
    public async Task ProcessClaimedAsync_ShutdownCancellationPropagatesWithoutProviderDisposition()
    {
        var now = DateTimeOffset.UtcNow;
        var recorder = new RecordingRuntimeRepository();
        var processor = new CancellingProcessor();
        var tracker = new CountingProviderOutageTracker();
        var runner = new TriageJobRunner(
            recorder,
            new StaticConfigurationRepository(),
            processor,
            new FixedTimeProvider(now),
            tracker);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ProcessClaimedAsync(
            CreateJob(now, attempt: 1),
            "worker",
            new TriageJobProcessingSettings(MaxAttempts: 3, RetryDelay: TimeSpan.FromSeconds(5)),
            cancellation.Token));

        Assert.Empty(recorder.Failures);
        Assert.Equal(0, tracker.FailureCount);
    }

    private static TriageJob CreateJob(DateTimeOffset now, int attempt) =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Processing, attempt, "worker",
            now.AddMinutes(1), null, null, null, "config", now, now);

    private sealed class StaticConfigurationRepository : ITriageConfigurationRepository
    {
        public int ReadCount { get; private set; }

        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult<TriageConfiguration>(null!);

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken)
        {
            ReadCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<TriageConfiguration>(null!);
        }
    }

    private sealed class ProviderFailingProcessor(Exception exception) : IClaimedTriageJobProcessor
    {
        public Task ProcessAsync(TriageJob job, TriageConfiguration configuration, string workerId, CancellationToken cancellationToken) =>
            Task.FromException(exception);
    }

    private sealed class CountingProcessor : IClaimedTriageJobProcessor
    {
        public int CallCount { get; private set; }

        public Task ProcessAsync(TriageJob job, TriageConfiguration configuration, string workerId, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class CancellingProcessor : IClaimedTriageJobProcessor
    {
        public Task ProcessAsync(TriageJob job, TriageConfiguration configuration, string workerId, CancellationToken cancellationToken) =>
            Task.FromCanceled(cancellationToken);
    }

    private sealed class CountingProviderOutageTracker : IProviderOutageTracker
    {
        public int FailureCount { get; private set; }

        public bool IsBackpressured => FailureCount > 0;

        public TimeSpan RetryDelay => TimeSpan.FromSeconds(60);

        public void RecordProviderFailure() => FailureCount++;

        public void RecordProviderSuccess() => FailureCount = 0;
    }

    private sealed class RecordingRuntimeRepository : ITriageJobRuntimeRepository
    {
        public List<TriageJobAttemptFailure> Failures { get; } = [];

        public Task<TriageJob?> ClaimNextAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken) => Task.FromResult<TriageJob?>(null);

        public Task<bool> RenewLeaseAsync(TriageJob job, string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task RecordAttemptFailureAsync(TriageJob job, string workerId, TriageJobAttemptFailure failure, CancellationToken cancellationToken)
        {
            Failures.Add(failure);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
