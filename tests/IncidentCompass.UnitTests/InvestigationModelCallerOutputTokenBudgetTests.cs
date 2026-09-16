using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The provider's output limit carries what is left of the attempt token budget. Before a call the
/// remainder is the budget, minus what the attempt spent, minus the estimated prompt; the request
/// asks for the smaller of that and the route's own limit, a call with nothing left is refused before
/// dispatch, and a fallback hop recomputes from the usage that includes the failed call.
/// </summary>
public sealed class InvestigationModelCallerOutputTokenBudgetTests
{
    private const string PrimaryRouteId = "report-chat";
    private const string FallbackRouteId = "backup-chat";

    /// <summary>"Investigate." is twelve characters, which the estimator counts as three tokens.</summary>
    private const string Prompt = "Investigate.";

    private const int PromptTokens = 3;

    [Fact]
    public async Task CompleteAsync_RouteAllowsMoreThanIsLeft_RequestsTheRemainder()
    {
        var model = new RecordingModelClient();
        var context = CreateContext(maxTokens: 1000, primaryMaxOutputTokens: 8000);

        await CreateCaller(model, spentTokens: 200).CompleteAsync(
            context, context.Configuration.Routes[PrimaryRouteId], Messages(), tools: null, TestContext.Current.CancellationToken);

        Assert.Equal(1000 - 200 - PromptTokens, Assert.Single(model.Requests).MaxOutputTokens);
    }

    [Fact]
    public async Task CompleteAsync_RouteLimitIsSmallerThanWhatIsLeft_RequestsTheRouteLimit()
    {
        var model = new RecordingModelClient();
        var context = CreateContext(maxTokens: 100_000, primaryMaxOutputTokens: 100);

        await CreateCaller(model, spentTokens: 200).CompleteAsync(
            context, context.Configuration.Routes[PrimaryRouteId], Messages(), tools: null, TestContext.Current.CancellationToken);

        Assert.Equal(100, Assert.Single(model.Requests).MaxOutputTokens);
    }

    [Fact]
    public async Task CompleteAsync_RouteWithoutALimit_RequestsTheRemainder()
    {
        var model = new RecordingModelClient();
        var context = CreateContext(maxTokens: 5000, primaryMaxOutputTokens: null);

        await CreateCaller(model, spentTokens: 0).CompleteAsync(
            context, context.Configuration.Routes[PrimaryRouteId], Messages(), tools: null, TestContext.Current.CancellationToken);

        Assert.Equal(5000 - PromptTokens, Assert.Single(model.Requests).MaxOutputTokens);
    }

    [Fact]
    public async Task CompleteAsync_NothingLeftAfterTheEstimatedPrompt_IsRefusedBeforeDispatch()
    {
        var model = new RecordingModelClient();
        var writer = new RecordingLedgerWriter();
        var context = CreateContext(maxTokens: 1000, primaryMaxOutputTokens: 8000);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() =>
            CreateCaller(model, spentTokens: 1000 - PromptTokens, writer).CompleteAsync(
                context, context.Configuration.Routes[PrimaryRouteId], Messages(), tools: null, TestContext.Current.CancellationToken));

        Assert.Equal(TriageBudgetExhaustedException.MaxTokensReachedCode, exception.ErrorCode);
        Assert.Empty(model.Requests);
        Assert.Contains(writer.Requests, request =>
            request.EventType == TriageLedgerEventType.BudgetEvent &&
            request.Rationale!.StartsWith("max_tokens_reached_before_call", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteAsync_FallbackHop_RequestsTheRemainderAfterTheFailedCallIsCharged()
    {
        var model = new RecordingModelClient(PrimaryFailure(totalTokens: 400));
        var context = CreateContext(maxTokens: 1000, primaryMaxOutputTokens: 8000);

        await CreateCaller(model, spentTokens: 100).CompleteAsync(
            context, context.Configuration.Routes[PrimaryRouteId], Messages(), tools: null, TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Requests.Count);
        Assert.Equal(1000 - 100 - PromptTokens, model.Requests[0].MaxOutputTokens);
        Assert.Equal(1000 - 100 - 400 - PromptTokens, model.Requests[1].MaxOutputTokens);
        Assert.Equal("backup-model", model.Requests[1].Model);
    }

    [Fact]
    public async Task CompleteAsync_FallbackWithNothingLeft_IsNotTakenAndThePrimaryFailureStandsUncharged()
    {
        var model = new RecordingModelClient(PrimaryFailure(totalTokens: 1000 - 100 - PromptTokens));
        var writer = new RecordingLedgerWriter();
        var context = CreateContext(maxTokens: 1000, primaryMaxOutputTokens: 8000);

        var exception = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            CreateCaller(model, spentTokens: 100, writer).CompleteAsync(
                context, context.Configuration.Routes[PrimaryRouteId], Messages(), tools: null, TestContext.Current.CancellationToken));

        Assert.Single(model.Requests);
        Assert.Equal(ProviderFailureKind.Unavailable, Assert.IsType<AiModelException>(exception.InnerException).FailureKind);

        // The primary's accounting still rides on the exception for the attempt-failure path; it was
        // not also written here, so it cannot be charged twice.
        Assert.DoesNotContain(writer.Requests, request => request.EventType == TriageLedgerEventType.ModelCall);

        // Skipping the fallback is audit-visible, with the same reason token as a refused call.
        var budgetEvent = Assert.Single(writer.Requests, request => request.EventType == TriageLedgerEventType.BudgetEvent);
        Assert.StartsWith("max_tokens_reached_before_call", budgetEvent.Rationale, StringComparison.Ordinal);
        Assert.Contains(FallbackRouteId, budgetEvent.Rationale, StringComparison.Ordinal);
    }

    private static IReadOnlyList<AiChatMessage> Messages() => [new AiChatMessage(AiMessageRole.User, Prompt)];

    private static AiModelException PrimaryFailure(int totalTokens) =>
        new(
            "test-provider",
            "Provider failure.",
            failureKind: ProviderFailureKind.Unavailable,
            usage: new AiModelUsage(totalTokens, 0, totalTokens));

    private static InvestigationModelCaller CreateCaller(
        IAiModelClient model,
        int spentTokens,
        RecordingLedgerWriter? writer = null) =>
        new(
            model,
            new SpentTokensLedgerReader(spentTokens),
            new TriageLedgerAppender(writer ?? new RecordingLedgerWriter()),
            TimeProvider.System);

    private static TriageJobCallContext CreateContext(int maxTokens, int? primaryMaxOutputTokens)
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
                    MaxOutputTokens: primaryMaxOutputTokens,
                    ContextWindowTokens: null,
                    FallbackRouteId: FallbackRouteId),
                [FallbackRouteId] = new(
                    "Chat",
                    "backup-provider",
                    "backup-model",
                    Temperature: 0,
                    MaxOutputTokens: 8000,
                    ContextWindowTokens: null)
            },
            new OrchestratorSettings(
                "Investigate and publish a report.",
                PrimaryRouteId,
                ["delegate", "publish_report"],
                new OrchestratorBudgetSettings(MaxWorkers: 2, MaxTokens: maxTokens)),
            new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal),
            new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal),
            [],
            new IngestionSettings("local", ["tester"]),
            new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
            RedactionSettings.Default);

        return new TriageJobCallContext(
            new TriageJob(
                Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Processing, Attempt: 1, LockedBy: "worker-test",
                LockedUntilUtc: now.AddMinutes(5), NextAttemptAtUtc: null, LastErrorCode: null, LastErrorMessage: null,
                ConfigHash: "config-hash", CreatedAtUtc: now, UpdatedAtUtc: now),
            configuration,
            now,
            PrimaryRouteId,
            "orchestrator");
    }

    private sealed class RecordingModelClient(Exception? primaryFailure = null) : IAiModelClient
    {
        public List<AiModelRequest> Requests { get; } = [];

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Requests.Count == 1 && primaryFailure is not null)
            {
                return Task.FromException<AiModelResponse>(primaryFailure);
            }

            return Task.FromResult(new AiModelResponse(
                "A response.", request.Model, "test-provider", new AiModelUsage(1, 1, 2), request.CorrelationId, ProposedToolCalls: []));
        }
    }

    private sealed class SpentTokensLedgerReader(int spentTokens) : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(spentTokens, 0));

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job, string toolName, ToolRuleScope scope, TriageLedgerDecision decision, CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job, string toolName, ToolRuleScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FaultLedgerEntry>>([]);
    }

    private sealed class RecordingLedgerWriter : ITriageLedgerWriter
    {
        private long nextId;

        public List<TriageLedgerAppendRequest> Requests { get; } = [];

        public Task<TriageLedgerEntry> AppendAsync(TriageLedgerAppendRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TriageLedgerEntry(
                ++nextId, request.FaultId, request.JobId, request.Attempt, request.EventType,
                request.Role, request.ToolName, request.Rationale, request.Decision, request.DecisionReason,
                request.PayloadRef, request.ConfigHash, DateTimeOffset.UtcNow, request.ToolStatus,
                request.TokensDelta, request.WorkersDelta));
        }

        public async Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
            IReadOnlyList<TriageLedgerAppendRequest> requests,
            CancellationToken cancellationToken)
        {
            var entries = new List<TriageLedgerEntry>(requests.Count);
            foreach (var request in requests)
            {
                entries.Add(await AppendAsync(request, cancellationToken));
            }

            return entries;
        }
    }
}
