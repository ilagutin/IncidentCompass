using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Artifacts;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.Context;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The orchestrator loop turns each model turn into exactly one named outcome. These tests pin the
/// trajectory the loop exists for - delegate, then publish, then stop - and the fail-closed stops when
/// the rehydrated configuration names an orchestrator or role route it does not contain.
/// </summary>
public sealed class GovernedTriageInvestigationProcessorTests
{
    private const string OutputSchema = """
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

    private const string WorkerOutput = """
        {"keyFacts":["The checkout service timed out."],"candidateClassification":"SimpleKnownError","needsDeeperContext":false,"rationale":"Bounded known-error failure."}
        """;

    [Fact]
    public async Task ProcessAsync_DelegatesThenPublishesAndLeavesTheLoop()
    {
        var model = new ScriptedOrchestratorModel();
        var harness = CreateHarness(model);

        await harness.ProcessAsync();

        // Two orchestrator turns only: the delegate turn kept the loop going, the publish turn ended
        // it. A third orchestrator call would mean publication did not finish the investigation.
        Assert.Equal(2, model.OrchestratorCalls);
        Assert.Equal(1, model.WorkerCalls);
        var report = Assert.Single(harness.Reports.Published);
        Assert.Equal(TriageReportStatus.Completed, report.Status);
        Assert.Equal("Delegated analysis completed.", report.Summary);
        Assert.Contains(
            harness.LedgerWriter.Requests,
            request => request.EventType == TriageLedgerEventType.Delegated &&
                request.ToolName == OrchestratorToolNames.Delegate &&
                request.Role == "analysis");
        Assert.Contains(
            harness.LedgerWriter.Requests,
            request => request.EventType == TriageLedgerEventType.WorkerCompleted);
    }

    [Fact]
    public async Task ProcessAsync_OrchestratorRouteMissingFailsClosedBeforeAnyModelCall()
    {
        var model = new ScriptedOrchestratorModel();
        var harness = CreateHarness(model, orchestratorRouteId: "route-that-is-not-configured");

        var exception = await Assert.ThrowsAsync<TriageGovernanceDeniedException>(harness.ProcessAsync);

        Assert.Equal(TriageGovernanceDeniedException.OrchestratorRouteMissingCode, exception.ErrorCode);
        Assert.Contains("route-that-is-not-configured", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, model.OrchestratorCalls);
        Assert.Empty(harness.Reports.Published);
        // The runner must dead-letter this instead of spending the retry budget on the same config hash.
        Assert.Equal(
            TriageGovernanceDeniedException.OrchestratorRouteMissingCode,
            TriageNonRetryableFailureClassifier.TryGetErrorCode(exception));
    }

    [Fact]
    public async Task ProcessAsync_WorkerRouteMissingFailsClosedWithoutRunningTheWorker()
    {
        var model = new ScriptedOrchestratorModel();
        var harness = CreateHarness(model, analysisRouteId: "role-route-that-is-not-configured");

        var exception = await Assert.ThrowsAsync<TriageGovernanceDeniedException>(harness.ProcessAsync);

        Assert.Equal(TriageGovernanceDeniedException.WorkerRouteMissingCode, exception.ErrorCode);
        Assert.Contains("role-route-that-is-not-configured", exception.Message, StringComparison.Ordinal);
        Assert.Contains("analysis", exception.Message, StringComparison.Ordinal);
        // The orchestrator reached its delegate turn; the worker route is what could not be resolved,
        // so no worker model call was made and the orchestrator never got a delegate result to continue on.
        Assert.Equal(1, model.OrchestratorCalls);
        Assert.Equal(0, model.WorkerCalls);
        Assert.Empty(harness.Reports.Published);
        // The runner must dead-letter this instead of spending the retry budget on the same config hash.
        Assert.Equal(
            TriageGovernanceDeniedException.WorkerRouteMissingCode,
            TriageNonRetryableFailureClassifier.TryGetErrorCode(exception));
    }

    private static ProcessorHarness CreateHarness(
        IAiModelClient model,
        string orchestratorRouteId = "report-chat",
        string analysisRouteId = "analysis-chat")
    {
        var configuration = CreateConfiguration(orchestratorRouteId, analysisRouteId);
        var ledgerWriter = new RecordingLedgerWriter();
        var ledgerReader = new StaticLedgerReader();
        var appender = new TriageLedgerAppender(ledgerWriter);
        var timeProvider = new ConstantTimeProvider(DateTimeOffset.UtcNow);
        var modelCaller = new InvestigationModelCaller(model, ledgerReader, appender, timeProvider);
        var delegateExecutor = new AnalysisDelegateExecutor(
            new NoOpArtifactRepository(),
            appender,
            ledgerReader,
            new WorkerRoleRunner(
                modelCaller,
                new WorkerToolCallExecutor([], new ToolRuleEngine(ledgerReader), appender, new UnusedToolResultCommitter())),
            timeProvider);
        var reports = new RecordingReportRepository();
        var processor = new GovernedTriageInvestigationProcessor(
            new StaticInvestigationContextRepository(),
            modelCaller,
            delegateExecutor,
            new TriageReportPublisher(reports, new EmptyContextOutcomeRepository()),
            timeProvider);
        return new ProcessorHarness(processor, configuration, ledgerWriter, reports);
    }

    private static TriageConfiguration CreateConfiguration(string orchestratorRouteId, string analysisRouteId) =>
        TestTriageConfiguration.Create() with
        {
            Orchestrator = new OrchestratorSettings(
                "orchestrator instructions",
                orchestratorRouteId,
                [OrchestratorToolNames.Delegate, OrchestratorToolNames.PublishReport],
                new OrchestratorBudgetSettings(4, 100000, 120)),
            Roles = new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal)
            {
                ["analysis"] = new(analysisRouteId, "analysis instructions", [], OutputSchema)
            },
            Tools = new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal)
        };

    private static TriageJob CreateJob()
    {
        var now = DateTimeOffset.UtcNow;
        return new TriageJob(
            Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Processing, 1, "worker-test",
            now.AddMinutes(5), null, null, null, "config-hash", now, now);
    }

    private static TriageJobInvestigationContext CreateContext()
    {
        var now = DateTimeOffset.UtcNow;
        var faultId = Guid.NewGuid();
        var signal = new Signal(
            Guid.NewGuid(), "tenant", "tester", faultId, "fingerprint", 1, FingerprintStrength.Strong, true,
            null, false, null, null, null, null, null, "checkout", "test", null, "Error",
            "TimeoutException", "Checkout timed out", "summary", null, null, null, null, null,
            EmptyObject(), EmptyObject(), now, now, null);
        var fault = new Fault(
            faultId, signal.Id, "tenant", FaultStatus.Analyzing, "fingerprint", 1,
            FingerprintStrength.Strong, true, "checkout", "test", "Error", null, now, null, null);
        return new TriageJobInvestigationContext(fault, signal, []);
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static JsonElement Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class ProcessorHarness(
        GovernedTriageInvestigationProcessor processor,
        TriageConfiguration configuration,
        RecordingLedgerWriter ledgerWriter,
        RecordingReportRepository reports)
    {
        public RecordingLedgerWriter LedgerWriter { get; } = ledgerWriter;

        public RecordingReportRepository Reports { get; } = reports;

        public Task ProcessAsync() =>
            processor.ProcessAsync(CreateJob(), configuration, "worker-test", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Answers an orchestrator turn (the request carries the orchestrator tool surface) with delegate
    /// first and publish_report second, and any worker turn with schema-valid output.
    /// </summary>
    private sealed class ScriptedOrchestratorModel : IAiModelClient
    {
        public int OrchestratorCalls { get; private set; }

        public int WorkerCalls { get; private set; }

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            if (request.Tools is not { Count: > 0 })
            {
                WorkerCalls++;
                return Task.FromResult(Response(WorkerOutput, []));
            }

            OrchestratorCalls++;
            return Task.FromResult(OrchestratorCalls == 1
                ? Response("Delegate analysis.", [new AiToolCall(
                    "call-delegate",
                    OrchestratorToolNames.Delegate,
                    "v1",
                    Arguments("""{"role":"analysis","task":"Analyze the checkout timeout."}"""))])
                : Response("Publish the report.", [new AiToolCall(
                    "call-publish",
                    OrchestratorToolNames.PublishReport,
                    "v1",
                    Arguments("""
                        {"report_json":{"status":"Completed","summary":"Delegated analysis completed.","classification":"SimpleKnownError","confidence":"Medium","documentationFit":"Missing","evidence":[{"referenceId":"artifact:trigger-signal"}],"limitations":[],"recommendedNextAction":"Review the checkout logs."}}
                        """))]));
        }

        private static AiModelResponse Response(string content, IReadOnlyList<AiToolCall> toolCalls) =>
            new(content, "test-model", "test-provider", new AiModelUsage(1, 1, 2), "correlation", toolCalls);
    }

    private sealed class StaticInvestigationContextRepository : ITriageJobInvestigationContextRepository
    {
        public Task<TriageJobInvestigationContext> GetAsync(Guid jobId, int attempt, CancellationToken cancellationToken) =>
            Task.FromResult(CreateContext());
    }

    private sealed class RecordingReportRepository : ITriageReportRepository
    {
        public List<TriageReport> Published { get; } = [];

        public Task<Guid> PublishAsync(TriageJob job, string workerId, TriageReport report, CancellationToken cancellationToken)
        {
            Published.Add(report);
            return Task.FromResult(Guid.NewGuid());
        }
    }

    private sealed class EmptyContextOutcomeRepository : IReadOnlyContextOutcomeRepository
    {
        public Task<IReadOnlyList<ReadOnlyContextOutcome>> ReadCurrentAttemptAsync(
            Guid jobId, int attempt, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReadOnlyContextOutcome>>([]);
    }

    private sealed class NoOpArtifactRepository : ITriageArtifactRepository
    {
        public Task InsertAsync(TriageArtifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ReplaceJobLevelAsync(TriageArtifact artifact, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>The analysis role grants no tools, so no tool result is ever committed here.</summary>
    private sealed class UnusedToolResultCommitter : ITriageToolResultCommitter
    {
        public Task<TriageArtifact> CommitSucceededAsync(
            TriageToolResultCommitRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The analysis role grants no worker tools.");
    }

    private sealed class StaticLedgerReader : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(0, 0));

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
