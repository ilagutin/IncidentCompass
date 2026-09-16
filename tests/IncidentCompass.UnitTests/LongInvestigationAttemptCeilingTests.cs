using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Artifacts;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.Context;
using IncidentCompass.Application.Investigation.Reports.Fallback;
using IncidentCompass.Application.Investigation.Reports.Redaction;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// A long investigation on a slow but producing model is no longer stopped by a short total deadline.
/// The real orchestration loop, the real model caller and its budget gate run against a model that
/// makes the clock move sixty seconds on every call and needs fifteen calls (seven delegations, seven
/// worker turns and the publish turn) to finish. Under the default ceiling the attempt publishes past
/// the old 600-second mark; under an explicit legacy <c>MaxWallClockSeconds</c> of 600 it stops with
/// the existing before-call budget code, which the job runner dead-letters. Time only moves when the
/// model is called, so nothing here waits or races.
/// </summary>
public sealed class LongInvestigationAttemptCeilingTests
{
    private const int Delegations = 7;
    private static readonly TimeSpan PerCall = TimeSpan.FromSeconds(60);

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

    [Fact]
    public async Task ProcessAsync_DefaultCeiling_CompletesAnInvestigationThatRunsPastSixHundredSeconds()
    {
        var time = new ManualTimerTimeProvider();
        var startedAt = time.GetUtcNow();
        var model = new SlowProducingModel(time);
        var reports = new RecordingReportRepository();

        await CreateProcessor(model, time, reports).ProcessAsync(
            CreateJob(), CreateConfiguration(new OrchestratorBudgetSettings(MaxWorkers: 16, MaxTokens: 1_000_000)),
            "worker-test", TestContext.Current.CancellationToken);

        Assert.Equal((Delegations * 2) + 1, model.Calls);
        Assert.True(time.GetUtcNow() - startedAt > TimeSpan.FromSeconds(600));
        Assert.Equal(TriageReportStatus.Completed, Assert.Single(reports.Published).Status);
    }

    [Fact]
    public async Task ProcessAsync_LegacyMaxWallClockSecondsOf600_StopsBeforeTheNextCallWithTheExistingCode()
    {
        var time = new ManualTimerTimeProvider();
        var model = new SlowProducingModel(time);
        var reports = new RecordingReportRepository();
        var configuration = CreateConfiguration(
            new OrchestratorBudgetSettings(MaxWorkers: 16, MaxTokens: 1_000_000, MaxWallClockSeconds: 600));

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() =>
            CreateProcessor(model, time, reports).ProcessAsync(
                CreateJob(), configuration, "worker-test", TestContext.Current.CancellationToken));

        Assert.Equal(TriageBudgetExhaustedException.WallClockReachedBeforeCallCode, exception.ErrorCode);
        Assert.Contains("MaxWallClockSeconds", exception.Message, StringComparison.Ordinal);
        Assert.Equal(10, model.Calls);
        Assert.Empty(reports.Published);
    }

    private static GovernedTriageInvestigationProcessor CreateProcessor(
        IAiModelClient model,
        TimeProvider timeProvider,
        RecordingReportRepository reports)
    {
        var ledgerReader = new EmptyLedgerReader();
        var appender = new TriageLedgerAppender(new RecordingLedgerWriter());
        var modelCaller = new InvestigationModelCaller(model, ledgerReader, appender, timeProvider);
        var delegateExecutor = new AnalysisDelegateExecutor(
            new DiscardingArtifactRepository(),
            appender,
            ledgerReader,
            new WorkerRoleRunner(
                modelCaller,
                new WorkerToolCallExecutor([], new ToolRuleEngine(ledgerReader), appender, new UnusedToolResultCommitter(), timeProvider),
                appender),
            timeProvider);
        return new GovernedTriageInvestigationProcessor(
            new StaticInvestigationContextRepository(),
            modelCaller,
            delegateExecutor,
            new TriageReportPublisher(
                reports,
                new EmptyContextOutcomeRepository(),
                new EmptyCitedEvidenceRedactionRepository(),
                new EmptyAttemptModelFallbackRepository()),
            appender,
            timeProvider,
            new RecordingLogger<GovernedTriageInvestigationProcessor>());
    }

    private static TriageConfiguration CreateConfiguration(OrchestratorBudgetSettings budget) =>
        TestTriageConfiguration.Create() with
        {
            Orchestrator = new OrchestratorSettings(
                "orchestrator instructions",
                "report-chat",
                [OrchestratorToolNames.Delegate, OrchestratorToolNames.PublishReport],
                budget),
            Roles = new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal)
            {
                ["analysis"] = new("analysis-chat", "analysis instructions", [], OutputSchema)
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

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Every call costs sixty seconds of clock time before it answers. The orchestrator delegates
    /// <see cref="Delegations"/> times and then publishes; every worker turn answers with valid output.
    /// </summary>
    private sealed class SlowProducingModel(ManualTimerTimeProvider time) : IAiModelClient
    {
        private int orchestratorCalls;

        public int Calls { get; private set; }

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            time.Advance(PerCall);
            if (request.Tools is not { Count: > 0 })
            {
                return Task.FromResult(Response("""
                    {"keyFacts":["The checkout service timed out."],"candidateClassification":"SimpleKnownError","needsDeeperContext":false,"rationale":"Bounded known-error failure."}
                    """, []));
            }

            orchestratorCalls++;
            if (orchestratorCalls <= Delegations)
            {
                return Task.FromResult(Response("Delegate analysis.", [new AiToolCall(
                    "call-delegate-" + orchestratorCalls,
                    OrchestratorToolNames.Delegate,
                    "v1",
                    Json("""{"role":"analysis","task":"Analyze the checkout timeout."}"""))]));
            }

            return Task.FromResult(Response("Publish the report.", [new AiToolCall(
                "call-publish",
                OrchestratorToolNames.PublishReport,
                "v1",
                Json("""
                    {"report_json":{"status":"Completed","summary":"Long investigation completed.","classification":"SimpleKnownError","confidence":"Medium","documentationFit":"Missing","evidence":[{"referenceId":"artifact:trigger-signal"}],"limitations":[],"recommendedNextAction":"Review the checkout logs."}}
                    """))]));
        }

        private static AiModelResponse Response(string content, IReadOnlyList<AiToolCall> toolCalls) =>
            new(content, "test-model", "test-provider", new AiModelUsage(1, 1, 2), "correlation", toolCalls);
    }

    private sealed class StaticInvestigationContextRepository : ITriageJobInvestigationContextRepository
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

    private sealed class UnusedToolResultCommitter : ITriageToolResultCommitter
    {
        public Task<TriageArtifact> CommitSucceededAsync(
            TriageToolResultCommitRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The analysis role grants no worker tools.");
    }

    private sealed class EmptyLedgerReader : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(TriageJob job, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(0, 0));

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

    private sealed class RecordingLedgerWriter : ITriageLedgerWriter
    {
        private long nextId;

        public Task<TriageLedgerEntry> AppendAsync(TriageLedgerAppendRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageLedgerEntry(
                ++nextId, request.FaultId, request.JobId, request.Attempt, request.EventType,
                request.Role, request.ToolName, request.Rationale, request.Decision, request.DecisionReason,
                request.PayloadRef, request.ConfigHash, DateTimeOffset.UtcNow, request.ToolStatus,
                request.TokensDelta, request.WorkersDelta));

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
