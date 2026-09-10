using System.Diagnostics;
using System.Text.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Observability;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

[Collection("Runtime telemetry")]
public sealed class InvestigationModelCallerTests
{
    [Fact]
    public async Task CompleteAsync_DoesNotAttachPromptContentToRuntimeActivity()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "IncidentCompass.Runtime",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);
        var writer = new RecordingLedgerWriter();
        var caller = CreateCaller(new StaticModelClient(new AiModelUsage(1, 1, 2)), writer, TimeProvider.System, telemetry: new RuntimeTelemetry());
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 60);
        const string secret = "prompt-body-must-not-export";

        await caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, secret)],
            tools: null,
            CancellationToken.None);

        var modelActivities = activities.Where(item => item.OperationName == "triage.model.call").ToArray();
        Assert.NotEmpty(modelActivities);
        Assert.All(modelActivities, activity =>
            Assert.DoesNotContain(activity.TagObjects, tag => tag.Value?.ToString()?.Contains(secret, StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task CompleteAsync_ContinuesWhenActivityExporterFails()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "IncidentCompass.Runtime",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = static _ => throw new InvalidOperationException("export unavailable")
        };
        ActivitySource.AddActivityListener(listener);
        var writer = new RecordingLedgerWriter();
        var caller = CreateCaller(new StaticModelClient(new AiModelUsage(1, 1, 2)), writer, TimeProvider.System, telemetry: new RuntimeTelemetry());
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 60);

        var response = await caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "Complete despite telemetry exporter failure.")],
            tools: null,
            CancellationToken.None);

        Assert.Equal("A concise response.", response.Content);
        Assert.Contains(writer.Requests, request => request.EventType == TriageLedgerEventType.ModelCall);
    }

    [Fact]
    public async Task CompleteAsync_PropagatesProviderIdAndNeutralReasoningPreference()
    {
        var writer = new RecordingLedgerWriter();
        var model = new StaticModelClient(new AiModelUsage(1, 1, 2));
        var caller = CreateCaller(model, writer, TimeProvider.System);
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 60);
        var route = context.Configuration.Routes[context.RouteId] with
        {
            Reasoning = AiReasoningLevel.Low
        };

        await caller.CompleteAsync(
            context,
            route,
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            CancellationToken.None);

        Assert.Equal("mock", model.LastRequest!.ProviderId);
        Assert.Equal(AiReasoningLevel.Low, model.LastRequest.Reasoning);
    }

    /// <summary>
    /// The route's configured provider reaches the durable row, and it reaches it beside the
    /// adapter identifier rather than instead of it. Without this the ledger says only which
    /// adapter answered, which every declared provider on that adapter shares, and cost accounting
    /// has nothing to tell two payers apart by.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_SuccessRecordsTheConfiguredProviderBesideTheAnsweringAdapter()
    {
        var writer = new RecordingLedgerWriter();
        var caller = CreateCaller(new StaticModelClient(new AiModelUsage(1, 1, 2)), writer, TimeProvider.System);
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 60);

        await caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            CancellationToken.None);

        var rationale = Assert
            .Single(writer.Requests, request => request.EventType == TriageLedgerEventType.ModelCall)
            .Rationale;
        using var metadata = JsonDocument.Parse(rationale!);
        Assert.Equal("test-provider", metadata.RootElement.GetProperty("provider").GetString());
        Assert.Equal("mock", metadata.RootElement.GetProperty("providerId").GetString());
    }

    /// <summary>
    /// A failed call is charged and audited like any other, so it has to name its payer too.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_FailureAccountingAlsoNamesTheConfiguredProvider()
    {
        var caller = CreateCaller(
            new FailingModelClient(new AiModelException(
                "test-provider",
                "Provider failed.",
                failureKind: ProviderFailureKind.OutputLimitReached,
                usage: new AiModelUsage(1, 1, 2))),
            new RecordingLedgerWriter(),
            TimeProvider.System);
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 60);

        var failure = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() => caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            CancellationToken.None));

        Assert.Equal("test-provider", failure.Accounting.Metadata.Provider);
        Assert.Equal("mock", failure.Accounting.Metadata.ProviderId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    public async Task CompleteAsync_SuccessRecordsProviderReportedReasoningTokensWithoutSeparateCharge(
        int reasoningTokens)
    {
        var writer = new RecordingLedgerWriter();
        var model = new StaticModelClient(new AiModelUsage(10, 20, 30, reasoningTokens));
        var caller = CreateCaller(model, writer, TimeProvider.System);
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 60);

        await caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            CancellationToken.None);

        var modelCall = Assert.Single(writer.Requests, request => request.EventType == TriageLedgerEventType.ModelCall);
        using var metadata = JsonDocument.Parse(modelCall.Rationale!);
        Assert.Equal(reasoningTokens, metadata.RootElement.GetProperty("reasoningTokens").GetInt32());
        var charge = Assert.Single(writer.Requests, request => request.EventType == TriageLedgerEventType.BudgetEvent);
        Assert.Equal(30, charge.TokensDelta);
    }

    [Fact]
    public async Task CompleteAsync_AllZeroProviderUsageChargesEstimatedTokens()
    {
        var writer = new RecordingLedgerWriter();
        var model = new StaticModelClient(new AiModelUsage(0, 0, 0));
        var caller = CreateCaller(model, writer, TimeProvider.System);
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 60);

        await caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "Summarize the incident.")],
            tools: null,
            CancellationToken.None);

        var modelCall = Assert.Single(writer.Requests, request => request.EventType == TriageLedgerEventType.ModelCall);
        using var metadata = JsonDocument.Parse(modelCall.Rationale!);
        Assert.Equal("estimate", metadata.RootElement.GetProperty("usageSource").GetString());
        Assert.True(metadata.RootElement.GetProperty("totalTokens").GetInt32() > 0);

        var charge = Assert.Single(writer.Requests, request => request.EventType == TriageLedgerEventType.BudgetEvent);
        Assert.True(charge.TokensDelta > 0);
    }

    [Fact]
    public async Task CompleteAsync_SuccessClearsProviderBackpressure()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new ProviderOutageTracker(
            Options.Create(new ProviderResilienceOptions { FailureThreshold = 1, BackpressureSeconds = 60 }),
            new ConstantTimeProvider(now));
        tracker.RecordProviderFailure();
        var writer = new RecordingLedgerWriter();
        var caller = CreateCaller(new StaticModelClient(new AiModelUsage(1, 1, 2)), writer, new ConstantTimeProvider(now), tracker);
        var context = CreateContext(now, maxWallClockSeconds: 60);

        await caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "Recover provider availability.")],
            tools: null,
            CancellationToken.None);

        Assert.False(tracker.IsBackpressured);
    }
    [Fact]
    public async Task CompleteAsync_WallClockReachedBeforeCallDoesNotInvokeModel()
    {
        var now = DateTimeOffset.UtcNow;
        var writer = new RecordingLedgerWriter();
        var model = new StaticModelClient(new AiModelUsage(1, 1, 2));
        var caller = CreateCaller(model, writer, new ConstantTimeProvider(now));
        var context = CreateContext(now.AddSeconds(-2), maxWallClockSeconds: 1);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "This call should not start.")],
            tools: null,
            CancellationToken.None));

        Assert.Equal(TriageBudgetExhaustedException.WallClockReachedBeforeCallCode, exception.ErrorCode);
        Assert.Contains("wall-clock budget", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, model.CallCount);
        Assert.Contains(writer.Requests, request =>
            request.EventType == TriageLedgerEventType.BudgetEvent &&
            request.Rationale!.Contains("wall_clock_limit_reached_before_call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteAsync_WallClockExpiresBetweenGuardAndCallDoesNotInvokeModel()
    {
        var now = DateTimeOffset.UtcNow;
        var writer = new RecordingLedgerWriter();
        var model = new StaticModelClient(new AiModelUsage(1, 1, 2));
        var caller = CreateCaller(
            model,
            writer,
            new SequenceTimeProvider(now.AddMilliseconds(900), now.AddSeconds(1), now.AddSeconds(1)));
        var context = CreateContext(now, maxWallClockSeconds: 1);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "The boundary expires before dispatch.")],
            tools: null,
            CancellationToken.None));

        Assert.Equal(TriageBudgetExhaustedException.WallClockReachedDuringCallCode, exception.ErrorCode);
        Assert.Contains("MaxWallClockSeconds", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, model.CallCount);
        Assert.Contains(writer.Requests, request =>
            request.EventType == TriageLedgerEventType.BudgetEvent &&
            request.Rationale!.Contains("wall_clock_limit_reached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteAsync_WallClockCancelAfterStopsMidCall()
    {
        var writer = new RecordingLedgerWriter();
        var model = new WaitingModelClient();
        var caller = CreateCaller(model, writer, TimeProvider.System);
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 1);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "Wait until cancellation.")],
            tools: null,
            CancellationToken.None));

        Assert.Equal(TriageBudgetExhaustedException.WallClockReachedDuringCallCode, exception.ErrorCode);
        Assert.Contains("MaxWallClockSeconds", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, model.CallCount);
        Assert.Contains(writer.Requests, request =>
            request.EventType == TriageLedgerEventType.BudgetEvent &&
            request.Rationale!.Contains("wall_clock_limit_reached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteAsync_TokenBudgetReachedBeforeCallRaisesItsOwnBudgetErrorCode()
    {
        var now = DateTimeOffset.UtcNow;
        var writer = new RecordingLedgerWriter();
        var model = new StaticModelClient(new AiModelUsage(1, 1, 2));
        var caller = CreateCaller(model, writer, new ConstantTimeProvider(now), spentTokens: 100000);
        var context = CreateContext(now, maxWallClockSeconds: 60);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "The token budget is already spent.")],
            tools: null,
            CancellationToken.None));

        Assert.Equal(TriageBudgetExhaustedException.MaxTokensReachedCode, exception.ErrorCode);
        Assert.Contains("token budget", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, model.CallCount);
        Assert.Contains(writer.Requests, request =>
            request.EventType == TriageLedgerEventType.BudgetEvent &&
            request.Rationale!.Contains("max_tokens_reached_before_call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteAsync_ContextWindowExceededRaisesItsOwnBudgetErrorCode()
    {
        var now = DateTimeOffset.UtcNow;
        var writer = new RecordingLedgerWriter();
        var model = new StaticModelClient(new AiModelUsage(1, 1, 2));
        var caller = CreateCaller(model, writer, new ConstantTimeProvider(now));
        var context = CreateContext(now, maxWallClockSeconds: 60, contextWindowTokens: 2);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "This prompt is larger than the configured context window.")],
            tools: null,
            CancellationToken.None));

        Assert.Equal(TriageBudgetExhaustedException.ContextWindowExceededCode, exception.ErrorCode);
        Assert.Contains("context window", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, model.CallCount);
        Assert.Contains(writer.Requests, request =>
            request.EventType == TriageLedgerEventType.BudgetEvent &&
            request.Rationale!.Contains("context_window_exceeded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteAsync_FailedProviderCallReturnsAccountingWithoutAppendingIt()
    {
        var writer = new RecordingLedgerWriter();
        var caller = CreateCaller(
            new FailingModelClient(new AiModelException(
                "test-provider",
                "Upstream response body must not be persisted.",
                failureKind: ProviderFailureKind.OutputLimitReached,
                usage: new AiModelUsage(1200, 2396, 3596, ReasoningTokens: 2000),
                returnedModel: "returned-model")),
            writer,
            TimeProvider.System);
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 60);

        var failure = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() => caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            CancellationToken.None));

        Assert.IsType<AiModelException>(failure.InnerException);
        Assert.Equal(3596, failure.Accounting.ChargeTokens);
        Assert.Equal("failed", failure.Accounting.Metadata.Outcome);
        Assert.Equal("provider_output_limit_reached", failure.Accounting.Metadata.ErrorCode);
        Assert.Equal("returned-model", failure.Accounting.Metadata.Model);
        Assert.Equal(0, failure.Accounting.Metadata.ProposedToolCallCount);
        Assert.Equal(2000, failure.Accounting.Metadata.ReasoningTokens);
        Assert.Empty(writer.Requests);
    }

    [Theory]
    [InlineData(0, "provider", 0)]
    [InlineData(null, "unknown", null)]
    public async Task CompleteAsync_FailedUsagePreservesZeroAndDoesNotFabricateUnknownCharge(
        int? totalTokens,
        string expectedUsageSource,
        int? expectedCharge)
    {
        var usage = totalTokens is null ? null : new AiModelUsage(0, 0, totalTokens);
        var writer = new RecordingLedgerWriter();
        var caller = CreateCaller(
            new FailingModelClient(new AiModelException(
                "test-provider",
                "Failed.",
                failureKind: ProviderFailureKind.InvalidResponse,
                usage: usage)),
            writer,
            TimeProvider.System);
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 60);

        var failure = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() => caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            CancellationToken.None));

        Assert.Equal(expectedUsageSource, failure.Accounting.Metadata.UsageSource);
        Assert.Equal(totalTokens, failure.Accounting.Metadata.TotalTokens);
        Assert.Equal(expectedCharge, failure.Accounting.ChargeTokens);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public async Task CompleteAsync_SuccessUsesRequiredAtomicLedgerBatch()
    {
        var writer = new RecordingLedgerWriter();
        var caller = CreateCaller(new StaticModelClient(new AiModelUsage(1, 2, 3)), writer, TimeProvider.System);
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 60);

        await caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            CancellationToken.None);

        Assert.Equal(1, writer.BatchCallCount);
        Assert.Collection(
            writer.Requests,
            request => Assert.Equal(TriageLedgerEventType.ModelCall, request.EventType),
            request => Assert.Equal(TriageLedgerEventType.BudgetEvent, request.EventType));
        Assert.Equal(writer.Requests[0].PayloadRef, writer.Requests[1].PayloadRef);
    }

    [Fact]
    public async Task CompleteAsync_LedgerPersistenceFailureCarriesSuccessfulCallAccounting()
    {
        var persistenceFailure = new InvalidOperationException("Injected ledger persistence failure.");
        var writer = new RecordingLedgerWriter { BatchException = persistenceFailure };
        var model = new StaticModelClient(new AiModelUsage(1, 2, 3));
        var caller = CreateCaller(model, writer, TimeProvider.System);
        var context = CreateContext(TimeProvider.System.GetUtcNow(), maxWallClockSeconds: 60);

        var thrown = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() => caller.CompleteAsync(
            context,
            context.Configuration.Routes[context.RouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            CancellationToken.None));

        Assert.Same(persistenceFailure, thrown.InnerException);
        Assert.Equal("success", thrown.Accounting.Metadata.Outcome);
        Assert.Null(thrown.Accounting.Metadata.ErrorCode);
        Assert.Equal(3, thrown.Accounting.Metadata.TotalTokens);
        Assert.Equal(3, thrown.Accounting.ChargeTokens);
        Assert.Equal(thrown.Accounting.Metadata.CallId, thrown.Accounting.CallId);
        Assert.Equal($"model-call:{thrown.Accounting.CallId:N}", thrown.Accounting.PayloadRef);
        Assert.Equal(1, model.CallCount);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public async Task AppendModelCallAccountingAsync_CanceledPersistenceTokenPropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var writer = new RecordingLedgerWriter
        {
            BatchException = new OperationCanceledException(cancellation.Token)
        };
        var callId = Guid.NewGuid();
        var accounting = new InvestigationModelCallAccounting(
            callId,
            "orchestrator",
            new ModelCallLedgerMetadata(
                "orchestrator",
                "report-chat",
                "test-model",
                "test-provider",
                "provider",
                1,
                2,
                3,
                4,
                0,
                callId,
                "success",
                ErrorCode: null),
            ChargeTokens: 3);
        var appender = new TriageLedgerAppender(writer);

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            appender.AppendModelCallAccountingAsync(
                CreateJob(TimeProvider.System.GetUtcNow()),
                accounting,
                cancellation.Token));

        Assert.IsNotType<InvestigationModelCallFailureException>(thrown);
        Assert.Equal(cancellation.Token, thrown.CancellationToken);
        Assert.Empty(writer.Requests);
    }

    private static InvestigationModelCaller CreateCaller(
        IAiModelClient modelClient,
        RecordingLedgerWriter writer,
        TimeProvider timeProvider,
        IProviderOutageTracker? providerOutageTracker = null,
        IRuntimeTelemetry? telemetry = null,
        int spentTokens = 0)
    {
        return new InvestigationModelCaller(
            modelClient,
            new StaticLedgerReader(spentTokens),
            new TriageLedgerAppender(writer),
            timeProvider,
            providerOutageTracker,
            telemetry);
    }

    private static TriageJobCallContext CreateContext(
        DateTimeOffset attemptStartedAtUtc,
        int maxWallClockSeconds,
        int contextWindowTokens = 8192)
    {
        var configuration = CreateConfiguration(maxWallClockSeconds, contextWindowTokens);
        return new TriageJobCallContext(
            CreateJob(attemptStartedAtUtc),
            configuration,
            attemptStartedAtUtc,
            "report-chat",
            "orchestrator");
    }

    private static TriageConfiguration CreateConfiguration(int maxWallClockSeconds, int contextWindowTokens)
    {
        return new TriageConfiguration(
            "config-hash",
            new Dictionary<string, TriageProviderSettings>
            {
                ["mock"] = new("Mock", Endpoint: null, ApiKeySecretRef: null)
            },
            new Dictionary<string, TriageRouteSettings>
            {
                ["report-chat"] = new("Chat", "mock", "test-model", Temperature: 0, MaxOutputTokens: 100, ContextWindowTokens: contextWindowTokens)
            },
            new OrchestratorSettings(
                "Investigate and publish a report.",
                "report-chat",
                ["delegate", "publish_report"],
                new OrchestratorBudgetSettings(MaxWorkers: 2, MaxTokens: 100000, MaxWallClockSeconds: maxWallClockSeconds, MaxReprompts: 1)),
            new Dictionary<string, TriageRoleSettings>(),
            new Dictionary<string, TriageToolSettings>(),
            [],
            new IngestionSettings("local", ["tester"]),
            new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
            RedactionSettings.Default);
    }
    private static TriageJob CreateJob(DateTimeOffset now)
    {
        return new TriageJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TriageJobStatus.Processing,
            Attempt: 1,
            LockedBy: "worker-test",
            LockedUntilUtc: now.AddMinutes(5),
            NextAttemptAtUtc: null,
            LastErrorCode: null,
            LastErrorMessage: null,
            ConfigHash: "config-hash",
            CreatedAtUtc: now,
            UpdatedAtUtc: now);
    }

    private sealed class StaticModelClient(AiModelUsage usage) : IAiModelClient
    {
        public int CallCount { get; private set; }

        public AiModelRequest? LastRequest { get; private set; }

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AiModelResponse(
                "A concise response.",
                request.Model,
                "test-provider",
                usage,
                request.CorrelationId,
                ProposedToolCalls: []));
        }
    }

    private sealed class WaitingModelClient : IAiModelClient
    {
        public int CallCount { get; private set; }

        public async Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The waiting model should be cancelled before returning.");
        }
    }

    private sealed class FailingModelClient(Exception exception) : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken) =>
            Task.FromException<AiModelResponse>(exception);
    }

    private sealed class StaticLedgerReader(int spentTokens = 0) : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(spentTokens, 0));

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job,
            string toolName,
            string scope,
            TriageLedgerDecision decision,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job,
            string toolName,
            string scope,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FaultLedgerEntry>>([]);
    }

    private sealed class RecordingLedgerWriter : ITriageLedgerWriter
    {
        private long nextId;

        public List<TriageLedgerAppendRequest> Requests { get; } = [];

        public int BatchCallCount { get; private set; }

        public Exception? BatchException { get; init; }

        public Task<TriageLedgerEntry> AppendAsync(TriageLedgerAppendRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var entry = new TriageLedgerEntry(
                ++nextId,
                request.FaultId,
                request.JobId,
                request.Attempt,
                request.EventType,
                request.Role,
                request.ToolName,
                request.Rationale,
                request.Decision,
                request.DecisionReason,
                request.PayloadRef,
                request.ConfigHash,
                DateTimeOffset.UtcNow,
                request.ToolStatus,
                request.TokensDelta,
                request.WorkersDelta);
            return Task.FromResult(entry);
        }

        public async Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
            IReadOnlyList<TriageLedgerAppendRequest> requests,
            CancellationToken cancellationToken)
        {
            BatchCallCount++;
            if (BatchException is not null)
            {
                throw BatchException;
            }

            var entries = new List<TriageLedgerEntry>(requests.Count);
            foreach (var request in requests)
            {
                entries.Add(await AppendAsync(request, cancellationToken));
            }

            return entries;
        }
    }

    private sealed class ConstantTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] timestamps) : TimeProvider
    {
        private int index;

        public override DateTimeOffset GetUtcNow()
        {
            if (index >= timestamps.Length)
            {
                return timestamps[^1];
            }

            return timestamps[index++];
        }
    }
}
