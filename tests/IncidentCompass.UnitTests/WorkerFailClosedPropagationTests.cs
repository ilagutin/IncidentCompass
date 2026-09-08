using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Intake.Artifacts;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The worker loop reprompts only failures the model itself can correct: its own output failing
/// schema validation. Attempt budgets and governance denials are backend fail-closed stops, so they
/// must leave the loop under their own error code and let the runner dead-letter the attempt instead
/// of being spent as a reprompt turn.
/// </summary>
public sealed class WorkerFailClosedPropagationTests
{
    private const string ProbeToolName = "worker_probe";

    private const string OutputSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": { "keyFacts": { "type": "array", "items": { "type": "string" } } },
          "required": ["keyFacts"]
        }
        """;

    private const string ValidOutput = """{"keyFacts":["A grounded fact."]}""";

    [Fact]
    public async Task RunAsync_TurnLimitReachedRaisesItsOwnBudgetErrorCode()
    {
        // A worker that only ever proposes accepted tool calls never validates output, so it walks the
        // whole bounded turn allowance and must stop at the turn limit rather than loop forever.
        var model = new ScriptedModelClient(_ => ToolCallResponse());
        var harness = CreateHarness(model);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(
            () => harness.RunWorkerAsync());

        Assert.Equal(TriageBudgetExhaustedException.WorkerTurnLimitReachedCode, exception.ErrorCode);
        Assert.Equal("Worker exceeded the bounded tool/reprompt turn limit.", exception.Message);
        Assert.Equal(6, model.CallCount);
    }

    [Fact]
    public async Task RunAsync_GovernanceDenialLeavesTheWorkerLoopInsteadOfBeingReprompted()
    {
        // An unregistered tool is denied by the rule engine. The denial must escape the worker loop
        // unwrapped so the runner dead-letters it; reprompting a denied call would let the model retry
        // an action backend policy just refused.
        var model = new ScriptedModelClient(_ => ToolCallResponse("not_registered"));
        var harness = CreateHarness(model);

        var exception = await Assert.ThrowsAsync<TriageGovernanceDeniedException>(
            () => harness.RunWorkerAsync());

        Assert.Equal(TriageGovernanceDeniedException.WorkerToolDeniedCode, exception.ErrorCode);
        Assert.Equal(1, model.CallCount);
        Assert.Contains(
            harness.LedgerWriter.Requests,
            request => request.EventType == TriageLedgerEventType.PolicyDecision &&
                request.Decision == TriageLedgerDecision.Denied);
    }

    [Fact]
    public async Task RunAsync_BudgetExhaustionLeavesTheWorkerLoopInsteadOfBeingReprompted()
    {
        var model = new ScriptedModelClient(_ => ContentResponse(ValidOutput));
        var harness = CreateHarness(model, spentTokens: 100000);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(
            () => harness.RunWorkerAsync());

        Assert.Equal(TriageBudgetExhaustedException.MaxTokensReachedCode, exception.ErrorCode);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task RunAsync_SchemaInvalidOutputIsStillRepromptedThenWrappedAsARetryableFailure()
    {
        // The repromptable case the catch filter exists for must keep working: the worker gets exactly
        // MaxReprompts corrections, then the failure is wrapped as an ordinary retryable attempt fault
        // that carries no budget or governance error code.
        var model = new ScriptedModelClient(_ => ContentResponse("""{"keyFacts":"not an array"}"""));
        var harness = CreateHarness(model, maxReprompts: 2);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RunWorkerAsync());

        Assert.Contains("remained invalid after bounded reprompts", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, model.CallCount);
        Assert.Null(TriageNonRetryableFailureClassifier.TryGetErrorCode(exception));
    }

    [Fact]
    public async Task RunAsync_UnparsableOutputIsRepromptedAndRecovers()
    {
        var model = new ScriptedModelClient(
            call => call == 0 ? ContentResponse("not json at all") : ContentResponse(ValidOutput));
        var harness = CreateHarness(model);

        var output = await harness.RunWorkerAsync();

        Assert.Equal(ValidOutput, output);
        Assert.Equal(2, model.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_WorkerBudgetReachedRaisesItsOwnBudgetErrorCodeAndSkipsTheWorker()
    {
        var model = new ScriptedModelClient(_ => ContentResponse(ValidOutput));
        var harness = CreateHarness(model, workerCalls: 2, maxWorkers: 2);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(
            () => harness.DelegateAsync());

        Assert.Equal(TriageBudgetExhaustedException.MaxWorkersReachedCode, exception.ErrorCode);
        Assert.Equal("The triage attempt worker budget was reached before delegation.", exception.Message);
        Assert.Equal(0, model.CallCount);
        Assert.Contains(
            harness.LedgerWriter.Requests,
            request => request.EventType == TriageLedgerEventType.BudgetEvent &&
                request.Rationale!.Contains("max_workers_reached", StringComparison.Ordinal));
        Assert.DoesNotContain(
            harness.LedgerWriter.Requests,
            request => request.EventType == TriageLedgerEventType.Delegated);
    }

    private static WorkerHarness CreateHarness(
        IAiModelClient model,
        int spentTokens = 0,
        int workerCalls = 0,
        int maxWorkers = 4,
        int maxReprompts = 1)
    {
        var configuration = CreateConfiguration(maxWorkers, maxReprompts);
        var ledgerWriter = new RecordingLedgerWriter();
        var ledgerReader = new StaticLedgerReader(spentTokens, workerCalls);
        var appender = new TriageLedgerAppender(ledgerWriter);
        var timeProvider = new ConstantTimeProvider(DateTimeOffset.UtcNow);
        var runner = new WorkerRoleRunner(
            new InvestigationModelCaller(model, ledgerReader, appender, timeProvider),
            new WorkerToolCallExecutor(
                [new ProbeTool()],
                new ToolRuleEngine(ledgerReader),
                appender,
                new NoOpToolResultCommitter()));
        var delegateExecutor = new AnalysisDelegateExecutor(
            new NoOpArtifactRepository(),
            appender,
            ledgerReader,
            runner,
            timeProvider);
        return new WorkerHarness(configuration, runner, delegateExecutor, ledgerWriter);
    }

    private static TriageConfiguration CreateConfiguration(int maxWorkers, int maxReprompts) =>
        TestTriageConfiguration.Create() with
        {
            Orchestrator = new OrchestratorSettings(
                "orchestrator instructions",
                "report-chat",
                ["delegate", "publish_report"],
                new OrchestratorBudgetSettings(maxWorkers, 100000, 120, maxReprompts)),
            Roles = new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal)
            {
                ["analysis"] = new("analysis-chat", "analysis instructions", [ProbeToolName], OutputSchema)
            },
            Tools = new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal)
            {
                [ProbeToolName] = new("internal", null, null, null)
            }
        };

    private static AiModelResponse ContentResponse(string content) =>
        new(content, "test-model", "test-provider", new AiModelUsage(1, 1, 2), "correlation", ProposedToolCalls: []);

    private static AiModelResponse ToolCallResponse(string toolName = ProbeToolName) =>
        new(
            string.Empty,
            "test-model",
            "test-provider",
            new AiModelUsage(1, 1, 2),
            "correlation",
            [new AiToolCall("call-1", toolName, "v1", EmptyObject())]);

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static TriageJob CreateJob()
    {
        var now = DateTimeOffset.UtcNow;
        return new TriageJob(
            Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Processing, 1, "worker-test",
            now.AddMinutes(5), null, null, null, "config-hash", now, now);
    }

    private static TriageJobInvestigationContext CreateContext(Guid faultId)
    {
        var now = DateTimeOffset.UtcNow;
        var signal = new Signal(
            Guid.NewGuid(), "tenant", "tester", faultId, "fingerprint", 1, FingerprintStrength.Strong, true,
            null, false, null, null, null, null, null, "checkout", "test", null, "Error",
            "TimeoutException", "Probe timed out", "summary", null, null, null, null, null,
            EmptyObject(), EmptyObject(), now, now, null);
        var fault = new Fault(
            faultId, signal.Id, "tenant", FaultStatus.Analyzing, "fingerprint", 1,
            FingerprintStrength.Strong, true, "checkout", "test", "Error", null, now, null, null);
        return new TriageJobInvestigationContext(fault, signal, []);
    }

    private sealed class WorkerHarness(
        TriageConfiguration configuration,
        WorkerRoleRunner runner,
        AnalysisDelegateExecutor delegateExecutor,
        RecordingLedgerWriter ledgerWriter)
    {
        public RecordingLedgerWriter LedgerWriter { get; } = ledgerWriter;

        public Task<string> RunWorkerAsync()
        {
            var job = CreateJob();
            return runner.RunAsync(
                job,
                configuration,
                CreateContext(job.FaultId),
                "analysis",
                configuration.Roles["analysis"],
                "Investigate the probe.",
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
        }

        public Task<string> DelegateAsync()
        {
            var job = CreateJob();
            using var arguments = JsonDocument.Parse("""{"role":"analysis","task":"Investigate the probe."}""");
            return delegateExecutor.ExecuteAsync(
                job,
                configuration,
                CreateContext(job.FaultId),
                new AiToolCall("call-delegate", "delegate", "v1", arguments.RootElement.Clone()),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
        }
    }

    private sealed class ScriptedModelClient(Func<int, AiModelResponse> script) : IAiModelClient
    {
        public int CallCount { get; private set; }

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            var response = script(CallCount);
            CallCount++;
            return Task.FromResult(response);
        }
    }

    private sealed class ProbeTool : IImmediateAgentTool
    {
        public AiToolDefinition Definition { get; } = new(ProbeToolName, "Probe tool.", "v1", EmptyObject());

        public ToolValidationResult Validate(JsonElement arguments) => ToolValidationResult.Valid(arguments);

        public Task<ToolExecutionResult> ExecuteAsync(
            AgentToolExecutionContext context,
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ToolExecutionResult(ToolExecutionStatus.Succeeded, EmptyObject()));
    }

    private sealed class NoOpToolResultCommitter : ITriageToolResultCommitter
    {
        public Task<TriageArtifact> CommitSucceededAsync(
            TriageToolResultCommitRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TriageArtifact(
                Guid.NewGuid(), request.Job.Id, request.Job.Attempt, ArtifactKind.ToolResult,
                request.ToolName, request.Output, request.ContentHash, DateTimeOffset.UtcNow));
    }

    private sealed class NoOpArtifactRepository : ITriageArtifactRepository
    {
        public Task InsertAsync(TriageArtifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReplaceJobLevelAsync(TriageArtifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StaticLedgerReader(int spentTokens, int workerCalls) : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(spentTokens, workerCalls));

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job, string toolName, string scope, TriageLedgerDecision decision,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job, string toolName, string scope, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<TriageLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TriageLedgerEntry>>([]);
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

    private sealed class ConstantTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
