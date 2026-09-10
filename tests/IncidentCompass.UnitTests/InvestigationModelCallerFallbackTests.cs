using System.Text.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// A route may declare a fallback, and a call the provider fails in a way a different provider could
/// plausibly answer is retried once there. These tests pin the four properties that make that safe:
/// the second call shares the first call's deadline, both calls are charged, only the failure kinds
/// worth a second provider are retried, and a fallback answering says nothing about the provider
/// that failed.
/// </summary>
public sealed class InvestigationModelCallerFallbackTests
{
    private const string PrimaryRouteId = "report-chat";
    private const string FallbackRouteId = "backup-chat";

    public static TheoryData<ProviderFailureKind> FailedOverKinds => new()
    {
        ProviderFailureKind.Unavailable,
        ProviderFailureKind.GenerationTimeout
    };

    /// <summary>
    /// Every kind that is deliberately not failed over. Two reasons are represented: the failure is
    /// the model's own answer being wrong, so a second call is the same money spent twice, or the
    /// dispatch outcome is not known, so a second call is the opposite of failing closed.
    /// </summary>
    public static TheoryData<ProviderFailureKind> NotFailedOverKinds => new()
    {
        ProviderFailureKind.RejectedRequest,
        ProviderFailureKind.OutputLimitReached,
        ProviderFailureKind.InvalidResponse,
        ProviderFailureKind.AmbiguousInterruption,
        ProviderFailureKind.TransportFailure,
        ProviderFailureKind.Unknown
    };

    [Theory]
    [MemberData(nameof(FailedOverKinds))]
    public async Task CompleteAsync_FailureWorthASecondProviderCompletesOnTheFallbackRoute(
        ProviderFailureKind failureKind)
    {
        var writer = new RecordingLedgerWriter();
        var model = new ScriptedModelClient(CreateProviderFailure(failureKind, new AiModelUsage(9, 0, 9)));
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        var response = await caller.CompleteAsync(
            context,
            context.Configuration.Routes[PrimaryRouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            TestContext.Current.CancellationToken);

        Assert.Equal("A fallback response.", response.Content);
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal("primary-model", model.Requests[0].Model);
        Assert.Equal("backup-model", model.Requests[1].Model);
        Assert.Equal("backup-provider", model.Requests[1].ProviderId);
    }

    [Fact]
    public async Task CompleteAsync_FallbackSuccessLeavesBothCallsInTheLedgerAndChargesBoth()
    {
        var writer = new RecordingLedgerWriter();
        var model = new ScriptedModelClient(
            CreateProviderFailure(ProviderFailureKind.Unavailable, new AiModelUsage(9, 0, 9)));
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        await caller.CompleteAsync(
            context,
            context.Configuration.Routes[PrimaryRouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            TestContext.Current.CancellationToken);

        var modelCalls = ReadModelCallMetadata(writer);
        Assert.Equal(2, modelCalls.Length);

        var failed = modelCalls[0];
        Assert.Equal("failed", failed.Outcome);
        Assert.Equal(PrimaryRouteId, failed.RouteId);
        Assert.Equal("provider_unavailable", failed.ErrorCode);
        Assert.Null(failed.FallbackForRouteId);

        var succeeded = modelCalls[1];
        Assert.Equal("success", succeeded.Outcome);
        Assert.Equal(FallbackRouteId, succeeded.RouteId);
        Assert.Equal("backup-model", succeeded.Model);
        Assert.Equal(PrimaryRouteId, succeeded.FallbackForRouteId);

        // The failed call is not refunded, exempted or hidden: what the provider would invoice for
        // both calls is what the attempt is charged.
        var charges = writer.Requests
            .Where(request => request.EventType == TriageLedgerEventType.BudgetEvent)
            .Select(request => request.TokensDelta)
            .ToArray();
        Assert.Equal([9, 3], charges);
    }

    [Theory]
    [MemberData(nameof(NotFailedOverKinds))]
    public async Task CompleteAsync_FailureNotWorthASecondProviderStaysFailedOnItsOwnRoute(
        ProviderFailureKind failureKind)
    {
        var writer = new RecordingLedgerWriter();
        var model = new ScriptedModelClient(CreateProviderFailure(failureKind, usage: null));
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        var thrown = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[PrimaryRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                TestContext.Current.CancellationToken));

        Assert.Single(model.Requests);
        Assert.Equal(PrimaryRouteId, thrown.Accounting.Metadata.RouteId);
        Assert.Null(thrown.Accounting.Metadata.FallbackForRouteId);
        Assert.Equal(failureKind, ProviderOutageExceptionClassifier.FindFailureKind(thrown));

        // Unchanged from a route with no fallback at all: the failed call's accounting rides on the
        // exception for the attempt-failure path to make durable, and nothing was appended here.
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public async Task CompleteAsync_FailedFallbackRaisesThePrimarysDispositionAndTakesNoSecondHop()
    {
        var writer = new RecordingLedgerWriter();
        var model = new ScriptedModelClient(
            CreateProviderFailure(ProviderFailureKind.Unavailable, new AiModelUsage(9, 0, 9)),
            CreateProviderFailure(ProviderFailureKind.RejectedRequest, usage: null));
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        var thrown = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[PrimaryRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                TestContext.Current.CancellationToken));

        // One hop. The fallback route declares a fallback of its own, and it is not followed.
        Assert.Equal(2, model.Requests.Count);

        // The kind the job runner classifies is the primary's, so the disposition table stays true:
        // this attempt waits on an unavailable provider rather than dead-lettering on the second
        // call's rejection.
        Assert.Equal(
            ProviderFailureKind.Unavailable,
            ProviderOutageExceptionClassifier.FindFailureKind(thrown));

        // The accounting still owed is the fallback's; the primary's was made durable before the
        // second call was allowed to spend anything.
        Assert.Equal(FallbackRouteId, thrown.Accounting.Metadata.RouteId);
        Assert.Equal(PrimaryRouteId, thrown.Accounting.Metadata.FallbackForRouteId);
        var durable = Assert.Single(ReadModelCallMetadata(writer));
        Assert.Equal(PrimaryRouteId, durable.RouteId);
        Assert.Equal("failed", durable.Outcome);
    }

    /// <summary>
    /// The provider timeout is authoritative for one attempt, so the fallback must not start a
    /// deadline of its own. Asserted by construction rather than by racing two bounds: the token the
    /// fallback call is handed is the same token the primary call was handed, which is the budget
    /// gate's single cancellation for this call.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_FallbackCallReusesThePrimaryCallsDeadline()
    {
        var writer = new RecordingLedgerWriter();
        var model = new ScriptedModelClient(
            CreateProviderFailure(ProviderFailureKind.GenerationTimeout, usage: null));
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        await caller.CompleteAsync(
            context,
            context.Configuration.Routes[PrimaryRouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Tokens.Count);
        Assert.Equal(model.Tokens[0], model.Tokens[1]);
        Assert.True(model.Tokens[1].CanBeCanceled);
    }

    [Fact]
    public async Task CompleteAsync_CancelledCallIsNotFailedOver()
    {
        using var cancellation = new CancellationTokenSource();
        var writer = new RecordingLedgerWriter();
        var model = new ScriptedModelClient(
            CreateProviderFailure(ProviderFailureKind.Unavailable, usage: null))
        {
            CancelBeforeFailing = cancellation
        };
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[PrimaryRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                cancellation.Token));

        Assert.Single(model.Requests);
        Assert.Empty(writer.Requests);
    }

    [Fact]
    public async Task CompleteAsync_FallbackSuccessLeavesProviderBackpressureIntact()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new ProviderOutageTracker(
            Options.Create(new ProviderResilienceOptions { FailureThreshold = 1, BackpressureSeconds = 60 }),
            new ConstantTimeProvider(now));
        tracker.RecordProviderFailure();
        Assert.True(tracker.IsBackpressured);

        var writer = new RecordingLedgerWriter();
        var model = new ScriptedModelClient(
            CreateProviderFailure(ProviderFailureKind.Unavailable, usage: null));
        var caller = CreateCaller(model, writer, tracker, new ConstantTimeProvider(now));
        var context = CreateContext();

        await caller.CompleteAsync(
            context,
            context.Configuration.Routes[PrimaryRouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            TestContext.Current.CancellationToken);

        // Claiming stays paused. A fallback answering is evidence about the fallback's provider and
        // none at all about the one that just failed.
        Assert.True(tracker.IsBackpressured);
    }

    [Fact]
    public async Task CompleteAsync_PrimaryCallOnItsOwnRouteStillClearsProviderBackpressure()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new ProviderOutageTracker(
            Options.Create(new ProviderResilienceOptions { FailureThreshold = 1, BackpressureSeconds = 60 }),
            new ConstantTimeProvider(now));
        tracker.RecordProviderFailure();

        var writer = new RecordingLedgerWriter();
        var caller = CreateCaller(new ScriptedModelClient(failure: null), writer, tracker, new ConstantTimeProvider(now));
        var context = CreateContext();

        await caller.CompleteAsync(
            context,
            context.Configuration.Routes[PrimaryRouteId],
            [new AiChatMessage(AiMessageRole.User, "Investigate.")],
            tools: null,
            TestContext.Current.CancellationToken);

        Assert.False(tracker.IsBackpressured);
    }

    [Fact]
    public async Task CompleteAsync_UnchargeableFailedCallIsNotFailedOver()
    {
        var writer = new RecordingLedgerWriter
        {
            BatchException = new InvalidOperationException("The ledger is unavailable.")
        };
        var model = new ScriptedModelClient(
            CreateProviderFailure(ProviderFailureKind.Unavailable, usage: null));
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        var thrown = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[PrimaryRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                TestContext.Current.CancellationToken));

        // No second call, and the primary's failure propagates with its accounting still owed, so
        // the attempt-failure path persists it exactly as it does for a route with no fallback.
        Assert.Single(model.Requests);
        Assert.Equal(PrimaryRouteId, thrown.Accounting.Metadata.RouteId);
        Assert.Equal(
            ProviderFailureKind.Unavailable,
            ProviderOutageExceptionClassifier.FindFailureKind(thrown));
    }

    private static AiModelException CreateProviderFailure(ProviderFailureKind failureKind, AiModelUsage? usage) =>
        new(
            "test-provider",
            "Provider failure.",
            failureKind: failureKind,
            usage: usage);

    private static ModelCallLedgerMetadata[] ReadModelCallMetadata(RecordingLedgerWriter writer) =>
        writer.Requests
            .Where(request => request.EventType == TriageLedgerEventType.ModelCall)
            .Select(request => JsonSerializer.Deserialize<ModelCallLedgerMetadata>(request.Rationale!)!)
            .ToArray();

    private static InvestigationModelCaller CreateCaller(
        IAiModelClient modelClient,
        RecordingLedgerWriter writer,
        IProviderOutageTracker? providerOutageTracker = null,
        TimeProvider? timeProvider = null) =>
        new(
            modelClient,
            new StaticLedgerReader(),
            new TriageLedgerAppender(writer),
            timeProvider ?? TimeProvider.System,
            providerOutageTracker);

    /// <summary>
    /// A primary route with a fallback, and a fallback route that declares a fallback of its own so
    /// that a second hop would be observable if one were ever taken.
    /// </summary>
    private static TriageJobCallContext CreateContext()
    {
        var now = DateTimeOffset.UtcNow;
        var configuration = new TriageConfiguration(
            "config-hash",
            new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal)
            {
                ["primary-provider"] = new("Mock", Endpoint: null, ApiKeySecretRef: null),
                ["backup-provider"] = new("Mock", Endpoint: null, ApiKeySecretRef: null)
            },
            new Dictionary<string, TriageRouteSettings>(StringComparer.Ordinal)
            {
                [PrimaryRouteId] = new(
                    "Chat",
                    "primary-provider",
                    "primary-model",
                    Temperature: 0,
                    MaxOutputTokens: 100,
                    ContextWindowTokens: 8192,
                    FallbackRouteId: FallbackRouteId),
                [FallbackRouteId] = new(
                    "Chat",
                    "backup-provider",
                    "backup-model",
                    Temperature: 0,
                    MaxOutputTokens: 100,
                    ContextWindowTokens: 8192,
                    FallbackRouteId: "third-chat"),
                ["third-chat"] = new(
                    "Chat",
                    "backup-provider",
                    "third-model",
                    Temperature: 0,
                    MaxOutputTokens: 100,
                    ContextWindowTokens: 8192)
            },
            new OrchestratorSettings(
                "Investigate and publish a report.",
                PrimaryRouteId,
                ["delegate", "publish_report"],
                new OrchestratorBudgetSettings(MaxWorkers: 2, MaxTokens: 100000, MaxWallClockSeconds: 600, MaxReprompts: 1)),
            new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal),
            new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal),
            [],
            new IngestionSettings("local", ["tester"]),
            new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
            RedactionSettings.Default);

        return new TriageJobCallContext(
            new TriageJob(
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
                UpdatedAtUtc: now),
            configuration,
            now,
            PrimaryRouteId,
            "orchestrator");
    }

    /// <summary>
    /// Fails the first call with <paramref name="failure" /> and answers every later call, unless a
    /// second failure is supplied for the fallback call.
    /// </summary>
    private sealed class ScriptedModelClient(Exception? failure, Exception? fallbackFailure = null) : IAiModelClient
    {
        public List<AiModelRequest> Requests { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public CancellationTokenSource? CancelBeforeFailing { get; init; }

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            Requests.Add(request);
            Tokens.Add(cancellationToken);

            if (Requests.Count == 1 && failure is not null)
            {
                CancelBeforeFailing?.Cancel();
                return Task.FromException<AiModelResponse>(failure);
            }

            if (Requests.Count == 2 && fallbackFailure is not null)
            {
                return Task.FromException<AiModelResponse>(fallbackFailure);
            }

            return Task.FromResult(new AiModelResponse(
                "A fallback response.",
                request.Model,
                "answering-provider",
                new AiModelUsage(1, 2, 3),
                request.CorrelationId,
                ProposedToolCalls: []));
        }
    }

    private sealed class StaticLedgerReader : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(0, 0));

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

        public Task<IReadOnlyList<TriageLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId,
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TriageLedgerEntry>>([]);
    }

    private sealed class RecordingLedgerWriter : ITriageLedgerWriter
    {
        private long nextId;

        public List<TriageLedgerAppendRequest> Requests { get; } = [];

        public Exception? BatchException { get; init; }

        public Task<TriageLedgerEntry> AppendAsync(
            TriageLedgerAppendRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TriageLedgerEntry(
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
                request.WorkersDelta));
        }

        public async Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
            IReadOnlyList<TriageLedgerAppendRequest> requests,
            CancellationToken cancellationToken)
        {
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
}
