using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Intake.Artifacts;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.Context;
using IncidentCompass.Application.Investigation.Reports.Fallback;
using IncidentCompass.Application.Investigation.Reports.Redaction;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The real orchestrator loop, delegate executor, worker role runner, worker tool executor and model
/// caller, composed over in-memory ports, a scripted model and one scripted read tool named
/// <see cref="ProbeToolName"/>. Time moves only when a script advances <see cref="Time"/>.
/// </summary>
internal sealed class InvestigationProgressTestHarness
{
    public const string ProbeToolName = "probe";

    public const string OutputSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "keyFacts": { "type": "array", "items": { "type": "string" } },
            "candidateClassification": { "type": "string" },
            "needsDeeperContext": { "type": "boolean" },
            "rationale": { "type": "string" }
          },
          "required": ["keyFacts", "candidateClassification", "needsDeeperContext"]
        }
        """;

    public InvestigationProgressTestHarness(
        ScriptedInvestigationModel model,
        Func<int, string> probeOutput,
        bool probeEmitsArtifacts = false,
        int maxEquivalentCalls = OrchestratorBudgetSettings.DefaultMaxEquivalentCalls,
        int maxTurnsWithoutProgress = OrchestratorBudgetSettings.DefaultMaxTurnsWithoutProgress,
        int maxRecoveries = OrchestratorBudgetSettings.DefaultMaxRecoveries,
        int maxTurns = OrchestratorBudgetSettings.DefaultMaxTurns,
        int maxWorkers = 32)
    {
        Model = model;
        Probe = new ScriptedProbeTool(probeOutput, probeEmitsArtifacts);
        Reader = new WriterBackedLedgerReader(Writer);
        var reader = Reader;
        var appender = new TriageLedgerAppender(Writer);
        var modelCaller = new InvestigationModelCaller(model, reader, appender, Time);
        var toolExecutor = new WorkerToolCallExecutor(
            [Probe], new ToolRuleEngine(reader), appender, new AcceptingToolResultCommitter(), Time, logger: ToolLogger);
        var delegateExecutor = new AnalysisDelegateExecutor(
            new DiscardingArtifactRepository(),
            appender,
            reader,
            new WorkerRoleRunner(modelCaller, toolExecutor, appender),
            Time,
            DelegateLogger);
        Processor = new GovernedTriageInvestigationProcessor(
            new StaticContextRepository(),
            modelCaller,
            delegateExecutor,
            new TriageReportPublisher(
                Reports,
                new EmptyContextOutcomeRepository(),
                new EmptyCitedEvidenceRedactionRepository(),
                new EmptyAttemptModelFallbackRepository()),
            appender,
            Time,
            ProcessorLogger);
        Configuration = TestTriageConfiguration.Create() with
        {
            Orchestrator = new OrchestratorSettings(
                "orchestrator instructions",
                "report-chat",
                [OrchestratorToolNames.Delegate, OrchestratorToolNames.PublishReport],
                new OrchestratorBudgetSettings(
                    MaxWorkers: maxWorkers,
                    MaxTokens: 1_000_000,
                    MaxEquivalentCalls: maxEquivalentCalls,
                    MaxTurnsWithoutProgress: maxTurnsWithoutProgress,
                    MaxRecoveries: maxRecoveries,
                    MaxTurns: maxTurns)),
            Roles = new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal)
            {
                ["analysis"] = new("analysis-chat", "analysis instructions", [], OutputSchema),
                ["prober"] = new("analysis-chat", "prober instructions", [ProbeToolName], OutputSchema)
            },
            Tools = new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal)
            {
                [ProbeToolName] = new("internal", null, null, null)
            },
            Rules = []
        };
    }

    public ManualTimerTimeProvider Time { get; } = new();

    public RecordingWriter Writer { get; } = new();

    public WriterBackedLedgerReader Reader { get; }

    public RecordingReports Reports { get; } = new();

    public RecordingLogger<GovernedTriageInvestigationProcessor> ProcessorLogger { get; } = new();

    public RecordingLogger<AnalysisDelegateExecutor> DelegateLogger { get; } = new();

    public RecordingLogger<WorkerToolCallExecutor> ToolLogger { get; } = new();

    public ScriptedInvestigationModel Model { get; }

    public ScriptedProbeTool Probe { get; }

    public GovernedTriageInvestigationProcessor Processor { get; }

    public TriageConfiguration Configuration { get; }

    public Task ProcessAsync() => ProcessUntilCancelledAsync(TestContext.Current.CancellationToken);

    public Task ProcessUntilCancelledAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow();
        var job = new TriageJob(
            Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Processing, 1, "worker-test",
            now.AddMinutes(5), null, null, null, "config-hash", now, now);
        return Processor.ProcessAsync(job, Configuration, "worker-test", cancellationToken);
    }

    public TriageLedgerAppendRequest[] NoProgressEvents(string reason) =>
        Writer.Requests
            .Where(request => request.EventType == TriageLedgerEventType.BudgetEvent &&
                (request.Rationale ?? string.Empty).StartsWith("no_progress: " + reason, StringComparison.Ordinal))
            .ToArray();

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static AiModelResponse Response(string content, params AiToolCall[] toolCalls) =>
        new(content, "test-model", "test-provider", new AiModelUsage(1, 1, 2), "correlation", toolCalls);

    public static AiModelResponse DelegateTurn(string role, string task) =>
        Response("Delegate.", new AiToolCall(
            "call-delegate",
            OrchestratorToolNames.Delegate,
            "v1",
            Json(JsonSerializer.Serialize(new { role, task }))));

    public static AiModelResponse ProbeTurn(string argumentsJson) =>
        Response(string.Empty, new AiToolCall("call-probe", ProbeToolName, "v1", Json(argumentsJson)));

    public static AiModelResponse PublishTurn() =>
        Response("Publish.", new AiToolCall(
            "call-publish",
            OrchestratorToolNames.PublishReport,
            "v1",
            Json("""
                {"report_json":{"status":"Completed","summary":"Investigation completed.","classification":"SimpleKnownError","confidence":"Medium","documentationFit":"Missing","evidence":[{"referenceId":"artifact:trigger-signal"}],"limitations":[],"recommendedNextAction":"Review the checkout logs."}}
                """)));

    public static string WorkerOutput(string fact, string classification = "SimpleKnownError") =>
        JsonSerializer.Serialize(new
        {
            keyFacts = new[] { fact },
            candidateClassification = classification,
            needsDeeperContext = false,
            rationale = "Bounded analysis."
        });

    /// <summary>
    /// Answers orchestrator requests (they carry the orchestrator tool surface) from one script and
    /// worker requests from another. Both scripts receive a one-based call number and the request.
    /// </summary>
    internal sealed class ScriptedInvestigationModel(
        Func<int, AiModelRequest, AiModelResponse> orchestrator,
        Func<int, AiModelRequest, AiModelResponse> worker,
        Func<int, AiModelRequest, AiModelResponse>? recovery = null) : IAiModelClient
    {
        public const string DefaultRecoveryText = "Delegate a narrower task to a different role.";

        public int OrchestratorCalls { get; private set; }

        public int RecoveryCalls { get; private set; }

        public List<AiModelRequest> RecoveryRequests { get; } = [];

        public List<AiModelRequest> OrchestratorRequests { get; } = [];

        public int WorkerCalls { get; private set; }

        public List<string> OrchestratorToolMessages { get; } = [];

        public List<string> WorkerToolMessages { get; } = [];

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            // A recovery request is the tool-less one whose user message is the backend progress summary.
            if (request.Tools is null &&
                request.Messages.Count == 2 &&
                request.Messages[1].Content.StartsWith(InvestigationProgressSummaryBuilder.Header, StringComparison.Ordinal))
            {
                RecoveryRequests.Add(request);
                var recoveryCall = ++RecoveryCalls;
                return Task.FromResult(recovery?.Invoke(recoveryCall, request) ?? Response(DefaultRecoveryText));
            }

            var isOrchestrator = request.Tools?.Any(tool => tool.Name == OrchestratorToolNames.Delegate) == true;
            if (isOrchestrator)
            {
                OrchestratorRequests.Add(request);
            }

            var lastMessage = request.Messages[^1];
            if (lastMessage.Role == AiMessageRole.Tool)
            {
                (isOrchestrator ? OrchestratorToolMessages : WorkerToolMessages).Add(lastMessage.Content);
            }

            return Task.FromResult(isOrchestrator
                ? orchestrator(++OrchestratorCalls, request)
                : worker(++WorkerCalls, request));
        }
    }

    /// <summary>
    /// A read tool. Plain, it returns the scripted JSON. With <paramref name="emitsArtifacts"/> it
    /// mimics <c>memory_search</c>: the scripted text becomes one match whose durable payload is a draft
    /// and whose output item names that draft's fresh <c>artifactId</c>.
    /// </summary>
    internal sealed class ScriptedProbeTool(Func<int, string> output, bool emitsArtifacts) : IImmediateAgentTool
    {
        public int Executions { get; private set; }

        public AiToolDefinition Definition { get; } = new(ProbeToolName, "Scripted probe.", "v1", Json("{}"));

        public ToolValidationResult Validate(JsonElement arguments) => ToolValidationResult.Valid(arguments);

        public Task<ToolExecutionResult> ExecuteAsync(
            AgentToolExecutionContext context,
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            var text = output(++Executions);
            if (!emitsArtifacts)
            {
                return Task.FromResult(new ToolExecutionResult(ToolExecutionStatus.Succeeded, Json(text)));
            }

            var draft = new ToolArtifactDraft(
                ArtifactKind.RetrievedItem,
                ArtifactDomainRef.Create("memory", "probe-item"),
                new JsonObject { ["quote"] = text });
            var items = new JsonArray(new JsonObject { ["artifactId"] = draft.Id.ToString(), ["quote"] = text });
            return Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                Json(new JsonObject { ["matched"] = true, ["items"] = items }.ToJsonString()),
                Artifacts: [draft]));
        }
    }

    internal sealed class RecordingWriter : ITriageLedgerWriter
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

    internal sealed class RecordingReports : ITriageReportRepository
    {
        public List<TriageReport> Published { get; } = [];

        /// <summary>When set, a backend-authored report is refused as the repository would refuse it.</summary>
        public bool RefuseBackendAuthored { get; set; }

        public Task<Guid> PublishAsync(TriageJob job, string workerId, TriageReport report, CancellationToken cancellationToken)
        {
            if (RefuseBackendAuthored && report.BackendAuthored)
            {
                throw new TriageReportValidationException("A re-triage report must cite recurrence state evidence.");
            }

            Published.Add(report);
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class AcceptingToolResultCommitter : ITriageToolResultCommitter
    {
        public Task<TriageArtifact> CommitSucceededAsync(
            TriageToolResultCommitRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TriageArtifact(
                Guid.NewGuid(), request.Job.Id, request.Job.Attempt, ArtifactKind.ToolResult,
                "tool:" + request.ToolName, request.Output, request.ContentHash, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Reads attempt usage the way the PostgreSQL reader does, from the budget events written so far,
    /// plus <see cref="ExtraTokensSpent"/> a script can raise to exhaust the token budget.
    /// </summary>
    internal sealed class WriterBackedLedgerReader(RecordingWriter writer) : ITriageLedgerReader
    {
        public int ExtraTokensSpent { get; set; }

        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(TriageJob job, CancellationToken cancellationToken)
        {
            var budgetEvents = writer.Requests.Where(static row => row.EventType == TriageLedgerEventType.BudgetEvent).ToArray();
            return Task.FromResult(new TriageBudgetLedgerUsage(
                ExtraTokensSpent + budgetEvents.Sum(static row => row.TokensDelta ?? 0),
                budgetEvents.Sum(static row => row.WorkersDelta ?? 0)));
        }

        public Task<int> CountPolicyDecisionsAsync(
            TriageJob job, string toolName, ToolRuleScope scope, TriageLedgerDecision decision,
            CancellationToken cancellationToken) => Task.FromResult(0);

        public Task<bool> HasSuccessfulToolResultAsync(
            TriageJob job, string toolName, ToolRuleScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<FaultLedgerEntry>> ReadByFaultIdAsync(
            Guid faultId, string tenantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FaultLedgerEntry>>([]);
    }

    private sealed class StaticContextRepository : ITriageJobInvestigationContextRepository
    {
        public Task<TriageJobInvestigationContext> GetAsync(Guid jobId, int attempt, CancellationToken cancellationToken)
        {
            var now = DateTimeOffset.UtcNow;
            var faultId = Guid.NewGuid();
            var signal = new Signal(
                Guid.NewGuid(), "tenant", "tester", faultId, "fingerprint", 1, FingerprintStrength.Strong, true,
                null, false, null, null, null, null, null, "checkout", "test", null, "Error",
                "TimeoutException", "Checkout timed out", "summary", null, null, null, null, null,
                Json("{}"), Json("{}"), now, now, null);
            var fault = new Fault(
                faultId, signal.Id, "tenant", FaultStatus.Analyzing, "fingerprint", 1,
                FingerprintStrength.Strong, true, "checkout", "test", "Error", null, now, null, null);
            return Task.FromResult(new TriageJobInvestigationContext(fault, signal, []));
        }
    }

    private sealed class EmptyContextOutcomeRepository : IReadOnlyContextOutcomeRepository
    {
        public Task<IReadOnlyList<ReadOnlyContextOutcome>> ReadCurrentAttemptAsync(
            Guid jobId, int attempt, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReadOnlyContextOutcome>>([]);
    }

    private sealed class EmptyCitedEvidenceRedactionRepository : ICitedEvidenceRedactionRepository
    {
        public Task<IReadOnlyList<CitedEvidenceRedaction>> ReadCitedAsync(
            Guid jobId, int attempt, IReadOnlyCollection<string> referenceIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CitedEvidenceRedaction>>([]);
    }

    private sealed class EmptyAttemptModelFallbackRepository : IAttemptModelFallbackRepository
    {
        public Task<IReadOnlyList<AttemptModelFallback>> ReadCurrentAttemptAsync(
            Guid jobId, int attempt, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AttemptModelFallback>>([]);
    }

    private sealed class DiscardingArtifactRepository : ITriageArtifactRepository
    {
        public Task InsertAsync(TriageArtifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReplaceJobLevelAsync(TriageArtifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
