using System.Text.Json;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.UnitTests;

/// <summary>
/// <see cref="IAiModelClient" /> promises that a provider failure is normalized before it crosses
/// the port. The shipped adapters keep that promise, so these tests are about the ones that do not:
/// a test double, a future adapter, an adapter regressed by a refactor. The guarantee under test is
/// that the bounded caller converts a breach rather than trusting it never happens, so
/// <see cref="InvestigationModelCallFailureException" /> stays exhaustive for a failed model call
/// and no call goes unaccounted because an adapter threw the wrong type.
/// </summary>
public sealed class AiModelClientContractBreachTests
{
    private const string PrimaryRouteId = "report-chat";
    private const string FallbackRouteId = "backup-chat";

    /// <summary>
    /// The realistic case: an adapter that forgot one catch around its transport.
    /// </summary>
    [Fact]
    public Task CompleteAsync_RawHttpFailureIsAccountedAndClassified() =>
        AssertContractViolationAsync(new HttpRequestException("Connection reset by peer."));

    /// <summary>
    /// The same guarantee for a type that has nothing to do with transport, because the contract is
    /// about anything that is not normalized rather than about HTTP specifically.
    /// </summary>
    [Fact]
    public Task CompleteAsync_UnrelatedClientFailureIsAccountedAndClassified() =>
        AssertContractViolationAsync(new InvalidOperationException("The adapter reached an unexpected state."));

    private static async Task AssertContractViolationAsync(Exception breach)
    {
        var writer = new RecordingLedgerWriter();
        var model = new ScriptedModelClient(breach);
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        var thrown = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[PrimaryRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                TestContext.Current.CancellationToken));

        // The accounting a failed call owes exists and says what the row will say.
        Assert.Equal("failed", thrown.Accounting.Metadata.Outcome);
        Assert.Equal("provider_contract_violation", thrown.Accounting.Metadata.ErrorCode);
        Assert.Equal(PrimaryRouteId, thrown.Accounting.Metadata.RouteId);

        // The original exception is still reachable for a developer reading a stack trace, beneath
        // the normalized exception the adapter should have raised itself.
        var synthesized = Assert.IsType<AiModelException>(thrown.InnerException);
        Assert.Same(breach, synthesized.InnerException);

        // The row names no real provider, because the adapter that answered is the thing that
        // misbehaved and there is no honest value for it.
        Assert.Equal("unknown", thrown.Accounting.Metadata.Provider);
        Assert.Equal("unknown", thrown.Accounting.Metadata.UsageSource);
        Assert.Null(thrown.Accounting.ChargeTokens);

        // Unchanged from a normalized failure: the accounting rides on the exception for the
        // attempt-failure path to make durable, and nothing was appended here.
        Assert.Empty(writer.Requests);
    }

    /// <summary>
    /// A breach is unclassified, and unclassified is the one kind that is not failed over. A broken
    /// adapter must not spend a second provider's budget on every call it mishandles.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ContractViolationIsUnknownAndTakesNoFallbackHop()
    {
        var writer = new RecordingLedgerWriter();
        var model = new ScriptedModelClient(new HttpRequestException("Connection reset by peer."));
        var caller = CreateCaller(model, writer);
        var context = CreateContext();
        Assert.Equal(FallbackRouteId, context.Configuration.Routes[PrimaryRouteId].FallbackRouteId);

        var thrown = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[PrimaryRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            ProviderFailureKind.Unknown,
            ProviderOutageExceptionClassifier.FindFailureKind(thrown));

        // One request, on the primary route's model. The fallback route declares a real chat route
        // that would have answered, so a second call would be visible here if one were taken.
        var request = Assert.Single(model.Requests);
        Assert.Equal("primary-model", request.Model);
        Assert.Null(thrown.Accounting.Metadata.FallbackForRouteId);
    }

    /// <summary>
    /// A breach on the fallback call, which is the one place the conversion changes an attempt's
    /// disposition rather than only enriching it.
    /// </summary>
    /// <remarks>
    /// A raw exception from a fallback adapter used to miss the fail-over catch and escape the
    /// caller unwrapped, so the attempt ended unclassified and consumed a retry toward a dead-letter.
    /// Converted, it is caught, and the documented fail-over rule then applies to it unchanged: the
    /// primary's kind is what ended the call, so the runner reads <c>Unavailable</c> and takes the
    /// outage branch - process backpressure and a delayed retry that does not consume the attempt.
    /// That is the rule working, not the breach being excused: the fallback's own accounting is what
    /// the exception carries, and it names the violation.
    /// </remarks>
    [Fact]
    public async Task CompleteAsync_BreachOnTheFallbackCallStillRaisesThePrimarysDisposition()
    {
        var writer = new RecordingLedgerWriter();
        var model = new FallbackBreachModelClient(
            writer,
            new AiModelException(
                "test-provider",
                "Model provider is unavailable.",
                errorCode: "provider_unavailable",
                failureKind: ProviderFailureKind.Unavailable,
                usage: new AiModelUsage(9, 0, 9)),
            new HttpRequestException("Connection reset by peer."));
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        var thrown = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[PrimaryRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                TestContext.Current.CancellationToken));

        // The fail-over happened, and it stopped at one hop.
        Assert.Equal(2, model.Requests.Count);
        Assert.Equal("backup-model", model.Requests[1].Model);

        // What the runner classifies is the primary's kind, not the breach's.
        Assert.Equal(
            ProviderFailureKind.Unavailable,
            ProviderOutageExceptionClassifier.FindFailureKind(thrown));

        // What is still owed is the fallback's accounting, and it names the violation.
        Assert.Equal(FallbackRouteId, thrown.Accounting.Metadata.RouteId);
        Assert.Equal(PrimaryRouteId, thrown.Accounting.Metadata.FallbackForRouteId);
        Assert.Equal("provider_contract_violation", thrown.Accounting.Metadata.ErrorCode);

        // The primary's accounting was durable before the fallback was allowed to spend anything.
        // Counting ModelCall rows rather than all appends pins the ordering directly: none existed
        // when the primary ran, exactly one existed when the fallback ran.
        Assert.Equal([0, 1], model.ModelCallRowsAtCall);
        var appended = Assert.Single(ReadModelCallMetadata(writer));
        Assert.Equal(PrimaryRouteId, appended.RouteId);
        Assert.Equal("provider_unavailable", appended.ErrorCode);
    }

    /// <summary>
    /// The regression guard on the path that already worked: converting a breach must not change
    /// what a properly normalized provider failure produces.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_NormalizedProviderFailureAccountingIsUnchanged()
    {
        var writer = new RecordingLedgerWriter();
        var normalized = new AiModelException(
            "test-provider",
            "Provider refused the request.",
            errorCode: "provider_request_rejected",
            failureKind: ProviderFailureKind.RejectedRequest,
            usage: new AiModelUsage(11, 0, 11),
            returnedModel: "returned-model");
        var model = new ScriptedModelClient(normalized);
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        var thrown = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[PrimaryRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                TestContext.Current.CancellationToken));

        Assert.Equal("failed", thrown.Accounting.Metadata.Outcome);
        Assert.Equal("provider_request_rejected", thrown.Accounting.Metadata.ErrorCode);
        Assert.Equal("test-provider", thrown.Accounting.Metadata.Provider);
        Assert.Equal("returned-model", thrown.Accounting.Metadata.Model);
        Assert.Equal("provider", thrown.Accounting.Metadata.UsageSource);
        Assert.Equal(11, thrown.Accounting.ChargeTokens);
        Assert.Equal(
            ProviderFailureKind.RejectedRequest,
            ProviderOutageExceptionClassifier.FindFailureKind(thrown));

        // The adapter's own exception is the inner one. Asserting the type alone would pass on the
        // breach path too, which also produces an AiModelException inner, so the instance is what
        // distinguishes a normalized failure passed through from one synthesized around it.
        Assert.Same(normalized, thrown.InnerException);
        Assert.Empty(writer.Requests);
    }

    /// <summary>
    /// Cancellation still wins. Host shutdown is not a contract breach, and converting it would turn
    /// a clean drain into a dead-lettered job.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_HostCancellationStillPropagatesOperationCanceled()
    {
        using var hostCancellation = new CancellationTokenSource();
        var writer = new RecordingLedgerWriter();
        var model = new CancellingModelClient(hostCancellation);
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        var thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[PrimaryRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                hostCancellation.Token));

        // The call really did reach the client, so this is the cancellation raised from inside the
        // model call rather than an admission guard refusing before it. The token pins the rest: the
        // caller rethrows the adapter's own cancellation unchanged rather than raising a fresh one
        // carrying the linked call token it created for the deadline.
        Assert.True(model.Invoked);
        Assert.Equal(hostCancellation.Token, thrown.CancellationToken);
        Assert.Empty(writer.Requests);
    }

    /// <summary>
    /// A cancellation the adapter wrapped rather than raised is a contract violation, and this pins
    /// that rather than endorsing it.
    /// </summary>
    /// <remarks>
    /// The caller's two cancellation clauses match <see cref="OperationCanceledException" /> at the
    /// top of the exception, so a wrapped one misses both. The host token is genuinely cancelled
    /// here, mid-call, so this is a real shutdown: what should have been a clean drain ends instead
    /// as a recorded, dead-lettering failure attributed to the adapter. It is the defensible reading
    /// - the port says a client raises that exception, not that it hides one inside another - but it
    /// is a real cost, so the behaviour is pinned here rather than left to be discovered during a
    /// drain. Fixing it belongs in the adapter.
    /// </remarks>
    [Fact]
    public async Task CompleteAsync_WrappedCancellationDuringShutdownIsTreatedAsAContractViolation()
    {
        using var hostCancellation = new CancellationTokenSource();
        var writer = new RecordingLedgerWriter();
        var wrapped = new AggregateException(new OperationCanceledException(hostCancellation.Token));
        var model = new ScriptedModelClient(wrapped) { CancelBeforeFailing = hostCancellation };
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        var thrown = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[PrimaryRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                hostCancellation.Token));

        // The shutdown really was under way when the call ended, and it still did not propagate.
        Assert.True(hostCancellation.IsCancellationRequested);
        Assert.Equal("provider_contract_violation", thrown.Accounting.Metadata.ErrorCode);
        var synthesized = Assert.IsType<AiModelException>(thrown.InnerException);
        Assert.Same(wrapped, synthesized.InnerException);
    }

    /// <summary>
    /// A breach whose own chain carries a classified provider failure keeps that classification.
    /// This is the limit of the "unclassified" rule, and it is pinned so the rule is not read as
    /// absolute.
    /// </summary>
    /// <remarks>
    /// The synthesized exception is chained above the offender, and the outage classifier returns
    /// the first classified provider failure in the whole chain rather than the topmost one, so an
    /// <see cref="EmbeddingClientException" /> - the one concrete provider exception the caller's
    /// normalized-failure clause does not match - reaches the runner with its own kind intact. The
    /// outcome is coherent: the failure genuinely was classified and only its wrapper was wrong, so
    /// it is dispositioned on the real kind while the error code records that a chat client raised
    /// the wrong exception type. It does mean this path decides the error code always and the
    /// failure kind only when nothing else in the chain has decided it.
    /// </remarks>
    [Fact]
    public async Task CompleteAsync_ClassifiedFailureInTheBreachChainKeepsItsKind()
    {
        var writer = new RecordingLedgerWriter();
        var model = new ScriptedModelClient(new InvalidOperationException(
            "The adapter wrapped an embedding-shaped failure.",
            new EmbeddingClientException(
                "test-provider",
                "Model provider is unavailable.",
                errorCode: "provider_unavailable",
                failureKind: ProviderFailureKind.Unavailable)));
        var caller = CreateCaller(model, writer);

        // Deliberately the route that declares no fallback, so this test is about one call. The
        // resolved kind is Unavailable, and on a route with a fallback that would fail the call
        // over, which the fallback-breach test above covers separately.
        var context = CreateContext() with { RouteId = FallbackRouteId };

        var thrown = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[FallbackRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                TestContext.Current.CancellationToken));

        // The row still names the breach.
        Assert.Single(model.Requests);
        Assert.Equal("provider_contract_violation", thrown.Accounting.Metadata.ErrorCode);
        Assert.Equal("unknown", thrown.Accounting.Metadata.Provider);

        // The kind the Worker dispositions on is the one the chain already carried, not Unknown.
        Assert.Equal(
            ProviderFailureKind.Unavailable,
            ProviderOutageExceptionClassifier.FindFailureKind(thrown));
    }

    /// <summary>
    /// A normalized failure that is not the first entry of an <see cref="AggregateException" /> is
    /// lost, and this pins the loss rather than hiding it.
    /// </summary>
    /// <remarks>
    /// The caller finds a normalized failure by walking <see cref="Exception.InnerException" />, and
    /// an aggregate exposes only its first inner exception there. So a real billed provider failure
    /// aggregated behind another exception is recorded as an unclassified breach: its failure kind,
    /// its error code, its provider and its provider-reported token counts are all dropped, and a
    /// call the provider will invoice for produces an unpriced row. That is a known cost of
    /// containment and not a reason to teach the walk about aggregates, because a client that
    /// aggregates a normalized failure behind other exceptions is itself in breach of the port: what
    /// the contract asks for is the normalized exception, raised.
    /// </remarks>
    [Fact]
    public async Task CompleteAsync_NormalizedFailureBehindAnAggregateIsRecordedAsABreach()
    {
        var writer = new RecordingLedgerWriter();
        var billed = new AiModelException(
            "test-provider",
            "Provider refused the request.",
            errorCode: "provider_request_rejected",
            failureKind: ProviderFailureKind.RejectedRequest,
            usage: new AiModelUsage(11, 0, 11));
        var model = new ScriptedModelClient(new AggregateException(
            new InvalidOperationException("An unrelated fault the adapter aggregated first."),
            billed));
        var caller = CreateCaller(model, writer);
        var context = CreateContext();

        var thrown = await Assert.ThrowsAsync<InvestigationModelCallFailureException>(() =>
            caller.CompleteAsync(
                context,
                context.Configuration.Routes[PrimaryRouteId],
                [new AiChatMessage(AiMessageRole.User, "Investigate.")],
                tools: null,
                TestContext.Current.CancellationToken));

        // Everything the aggregated exception knew about the call is gone from the durable row.
        Assert.Equal("provider_contract_violation", thrown.Accounting.Metadata.ErrorCode);
        Assert.Equal("unknown", thrown.Accounting.Metadata.Provider);
        Assert.Equal("unknown", thrown.Accounting.Metadata.UsageSource);
        Assert.Null(thrown.Accounting.Metadata.TotalTokens);
        Assert.Equal(
            ProviderFailureKind.Unknown,
            ProviderOutageExceptionClassifier.FindFailureKind(thrown));
    }

    private static ModelCallLedgerMetadata[] ReadModelCallMetadata(RecordingLedgerWriter writer) =>
        writer.Requests
            .Where(request => request.EventType == TriageLedgerEventType.ModelCall)
            .Select(request => JsonSerializer.Deserialize<ModelCallLedgerMetadata>(request.Rationale!)!)
            .ToArray();

    private static InvestigationModelCaller CreateCaller(
        IAiModelClient modelClient,
        RecordingLedgerWriter writer) =>
        new(
            modelClient,
            new StaticLedgerReader(),
            new TriageLedgerAppender(writer),
            TimeProvider.System);

    /// <summary>
    /// A primary route that declares a real chat fallback, so a fail-over hop would be observable if
    /// one were ever taken for an unclassified failure.
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
                    ContextWindowTokens: 8192)
            },
            new OrchestratorSettings(
                "Investigate and publish a report.",
                PrimaryRouteId,
                ["delegate", "publish_report"],
                new OrchestratorBudgetSettings(
                    MaxWorkers: 2,
                    MaxTokens: 100000,
                    MaxWallClockSeconds: 600,
                    MaxReprompts: 1)),
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
    /// Fails every call with the supplied exception and records what it was asked for, so a second
    /// hop taken on a fallback route would be visible.
    /// </summary>
    private sealed class ScriptedModelClient(Exception failure) : IAiModelClient
    {
        public List<AiModelRequest> Requests { get; } = [];

        /// <summary>
        /// Cancelled just before the failure is raised, so a test can stage a host shutdown that
        /// begins during the call rather than before admission.
        /// </summary>
        public CancellationTokenSource? CancelBeforeFailing { get; init; }

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            Requests.Add(request);
            CancelBeforeFailing?.Cancel();
            return Task.FromException<AiModelResponse>(failure);
        }
    }

    /// <summary>
    /// Fails the primary call at the provider and the fallback call in breach of the port, recording
    /// how many <c>ModelCall</c> rows the ledger already held when each call was made so the order of
    /// the two is provable.
    /// </summary>
    private sealed class FallbackBreachModelClient(
        RecordingLedgerWriter writer,
        Exception primaryFailure,
        Exception fallbackBreach) : IAiModelClient
    {
        public List<AiModelRequest> Requests { get; } = [];

        public List<int> ModelCallRowsAtCall { get; } = [];

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            Requests.Add(request);
            ModelCallRowsAtCall.Add(
                writer.Requests.Count(appended => appended.EventType == TriageLedgerEventType.ModelCall));
            return Task.FromException<AiModelResponse>(
                Requests.Count == 1 ? primaryFailure : fallbackBreach);
        }
    }

    /// <summary>
    /// A well-behaved adapter observing host shutdown mid-call: it cancels the host source the way a
    /// shutting-down host would and reports the cancellation the contract asks for.
    /// </summary>
    private sealed class CancellingModelClient(CancellationTokenSource hostCancellation) : IAiModelClient
    {
        public bool Invoked { get; private set; }

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            Invoked = true;
            hostCancellation.Cancel();
            throw new OperationCanceledException(hostCancellation.Token);
        }
    }

    private sealed class StaticLedgerReader : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(0, 0));

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job,
            string toolName,
            ToolRuleScope scope,
            TriageLedgerDecision decision,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job,
            string toolName,
            ToolRuleScope scope,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId,
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FaultLedgerEntry>>([]);
    }

    private sealed class RecordingLedgerWriter : ITriageLedgerWriter
    {
        private long nextId;

        public List<TriageLedgerAppendRequest> Requests { get; } = [];

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
            var entries = new List<TriageLedgerEntry>(requests.Count);
            foreach (var request in requests)
            {
                entries.Add(await AppendAsync(request, cancellationToken));
            }

            return entries;
        }
    }
}
