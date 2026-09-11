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
using Microsoft.Extensions.Logging;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The orchestrator loop turns each model turn into exactly one named outcome. These tests pin the
/// trajectory the loop exists for - delegate, then publish, then stop - and the fail-closed stops when
/// the rehydrated configuration names an orchestrator or role route it does not contain.
/// </summary>
public sealed class GovernedTriageInvestigationProcessorTests
{
    /// <summary>
    /// One member of the closed family of mismatch refusals. Which value it names does not matter
    /// here; what matters is that a message carrying a backend-derived value still survives the
    /// diagnostic allowlist verbatim instead of collapsing to the content-free fallback.
    /// </summary>
    private static readonly string DocumentationFitMismatch =
        DocumentationFitDiagnostics.Mismatch(DocumentationFitStatus.StaleOnly);

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

    /// <summary>
    /// A worker quotes the tool results it was given, so its output is connector text by the time it
    /// leaves the role. It reaches durable state as the <c>WorkerOutput</c> artifact and reaches the
    /// model again as the delegate result the orchestrator's next turn is built from, and the ledger
    /// rationale is a third copy. All three have to be the redacted form: redacting only the row would
    /// leave the orchestrator holding what the row hides.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_WorkerOutputSecretReachesNeitherTheArtifactNorTheOrchestrator()
    {
        // Shaped to match the built-in AWS access-key rule: AKIA plus sixteen upper-case characters.
        const string seededSecret = "AKIADELEGATEPATH0000";
        var model = new ScriptedOrchestratorModel($$"""
            {"keyFacts":["The checkout node still holds {{seededSecret}} in configuration."],"candidateClassification":"SimpleKnownError","needsDeeperContext":false,"rationale":"Rotate {{seededSecret}} before closing."}
            """);
        var harness = CreateHarness(model);

        await harness.ProcessAsync();

        var artifact = Assert.Single(harness.Artifacts.Inserted);
        Assert.Equal(ArtifactKind.WorkerOutput, artifact.Kind);
        var storedPayload = artifact.RedactedPayload.GetRawText();
        Assert.DoesNotContain(seededSecret, storedPayload, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", storedPayload, StringComparison.Ordinal);
        // The rest of the worker's finding survives; only the credential is gone.
        Assert.Contains("still holds", storedPayload, StringComparison.Ordinal);

        // The delegate result is a tool message on the orchestrator's second turn, and its rationale
        // is a WorkerCompleted ledger entry. Neither may carry what the artifact dropped.
        Assert.Equal(2, model.OrchestratorCalls);
        Assert.Contains(
            model.RequestMessages,
            message => message.Contains("Rotate [REDACTED] before closing.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            model.RequestMessages,
            message => message.Contains(seededSecret, StringComparison.Ordinal));
        var completed = Assert.Single(
            harness.LedgerWriter.Requests,
            request => request.EventType == TriageLedgerEventType.WorkerCompleted);
        Assert.NotNull(completed.Rationale);
        Assert.DoesNotContain(seededSecret, completed.Rationale, StringComparison.Ordinal);
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

    [Fact]
    public async Task ProcessAsync_DocumentationFitMismatchRepromptLogsAndLedgersExactSafeReason()
    {
        var model = new ScriptedOrchestratorModel();
        var harness = CreateHarness(
            model,
            reportFailureMessage: DocumentationFitMismatch);

        await harness.ProcessAsync();

        var log = harness.Logger.Single(3401);
        Assert.Equal(LogLevel.Information, log.Level);
        Assert.Contains("publish_report_validation_failed", log.Message, StringComparison.Ordinal);
        Assert.Contains(DocumentationFitMismatch, log.Message, StringComparison.Ordinal);
        Assert.Contains("1/1", log.Message, StringComparison.Ordinal);

        var ledgerEvent = Assert.Single(
            harness.LedgerWriter.Requests,
            request => request.EventType == TriageLedgerEventType.BudgetEvent &&
                request.Rationale is { } rationale &&
                rationale.StartsWith("orchestrator_reprompt:", StringComparison.Ordinal));
        Assert.Equal("orchestrator", ledgerEvent.Role);
        Assert.Contains("publish_report_validation_failed", ledgerEvent.Rationale, StringComparison.Ordinal);
        Assert.Contains(DocumentationFitMismatch, ledgerEvent.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_UnknownReportValidationMessageUsesContentFreeFallback()
    {
        const string arbitraryExceptionMessage = "MODEL_CONTROLLED_REPORT_TEXT_MUST_NOT_ESCAPE";
        var model = new ScriptedOrchestratorModel();
        var harness = CreateHarness(model, reportFailureMessage: arbitraryExceptionMessage);

        await harness.ProcessAsync();

        var log = harness.Logger.Single(3401);
        Assert.Contains(OrchestratorRepromptDiagnostics.UnknownReportValidationFailure, log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(arbitraryExceptionMessage, log.Message, StringComparison.Ordinal);
        var ledgerEvent = Assert.Single(
            harness.LedgerWriter.Requests,
            request => request.Rationale is { } rationale &&
                rationale.StartsWith("orchestrator_reprompt:", StringComparison.Ordinal));
        Assert.Contains(OrchestratorRepromptDiagnostics.UnknownReportValidationFailure, ledgerEvent.Rationale, StringComparison.Ordinal);
        Assert.DoesNotContain(arbitraryExceptionMessage, ledgerEvent.Rationale, StringComparison.Ordinal);
    }

    /// <summary>
    /// A spent reprompt allowance is a bounded-run limit, not an ordinary fault: it must leave the
    /// loop as <see cref="TriageBudgetExhaustedException"/> under its own code so the runner
    /// dead-letters the attempt instead of spending a retry under `triage_job_attempt_failed`.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_SpentRepromptAllowanceFailsClosedUnderItsOwnBudgetCode()
    {
        var model = new ScriptedOrchestratorModel();
        var harness = CreateHarness(
            model,
            reportFailureMessage: DocumentationFitMismatch,
            reportFailureCount: 2);

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(harness.ProcessAsync);

        Assert.Equal(
            TriageBudgetExhaustedException.OrchestratorRepromptLimitReachedCode,
            exception.ErrorCode);
        // The validation failure the loop could not correct stays on the chain the classifier walks.
        Assert.IsType<TriageReportValidationException>(exception.InnerException);
        Assert.Empty(harness.Reports.Published);
        // The runner must dead-letter this instead of spending the retry budget on the same config hash.
        Assert.Equal(
            TriageBudgetExhaustedException.OrchestratorRepromptLimitReachedCode,
            TriageNonRetryableFailureClassifier.TryGetErrorCode(exception));

        // Exactly one reprompt was charged and ledgered; the turn that could not be corrected is
        // reported separately, so the cause of the exhaustion is not lost to the single error code.
        var repromptLog = harness.Logger.Single(3401);
        Assert.Contains("1/1", repromptLog.Message, StringComparison.Ordinal);
        var exhaustionLog = harness.Logger.Single(3403);
        Assert.Equal(LogLevel.Warning, exhaustionLog.Level);
        Assert.Contains("publish_report_validation_failed", exhaustionLog.Message, StringComparison.Ordinal);
        Assert.Contains(
            DocumentationFitMismatch,
            exhaustionLog.Message,
            StringComparison.Ordinal);
        Assert.Single(
            harness.LedgerWriter.Requests,
            request => request.Rationale is { } rationale &&
                rationale.StartsWith("orchestrator_reprompt:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A delegate call naming a role the configuration does not hold is a correctable refusal that
    /// costs a reprompt, and the value it named never leaves the backend.
    /// </summary>
    /// <remarks>
    /// Both halves used to be wrong in the same early return. The role came out of model-supplied
    /// tool-call arguments and went straight back into the tool result, which made it the one place
    /// in this codebase that reflected untrusted text. And the return skipped the worker-budget
    /// check and every ledger append, while the loop still counted the turn as delegated, so a model
    /// looping on an unknown role spent the whole turn allowance and left nothing in the ledger to
    /// explain it.
    /// </remarks>
    [Fact]
    public async Task ProcessAsync_UnknownDelegateRoleCostsARepromptAndNeverEchoesTheRoleItRefused()
    {
        const string hostileRole = "UNKNOWN_ROLE_MODEL_TEXT_MUST_NOT_ESCAPE";
        var model = new ScriptedOrchestratorModel(refusedFirstDelegateRole: hostileRole);
        var harness = CreateHarness(model);

        await harness.ProcessAsync();

        // The turn was charged as a correction and is durable under its own reprompt reason.
        var log = harness.Logger.Single(3401);
        Assert.Contains("delegate_validation_failed", log.Message, StringComparison.Ordinal);
        Assert.Contains(AnalysisDelegateExecutor.UnknownRoleMessage, log.Message, StringComparison.Ordinal);
        Assert.Contains("1/1", log.Message, StringComparison.Ordinal);
        var repromptEvent = Assert.Single(
            harness.LedgerWriter.Requests,
            request => request.EventType == TriageLedgerEventType.BudgetEvent &&
                request.Rationale is { } rationale &&
                rationale.StartsWith("orchestrator_reprompt:", StringComparison.Ordinal));
        Assert.Equal("orchestrator", repromptEvent.Role);
        Assert.Contains("delegate_validation_failed", repromptEvent.Rationale, StringComparison.Ordinal);

        // The refused turn delegated nothing, so only the corrected turn is on the ledger as one.
        var delegated = Assert.Single(
            harness.LedgerWriter.Requests,
            request => request.EventType == TriageLedgerEventType.Delegated);
        Assert.Equal("analysis", delegated.Role);

        // The role the model invented reaches neither the ledger, nor the log, nor the model again.
        Assert.DoesNotContain(
            harness.LedgerWriter.Requests,
            request => (request.Rationale ?? string.Empty).Contains(hostileRole, StringComparison.Ordinal) ||
                (request.Role ?? string.Empty).Contains(hostileRole, StringComparison.Ordinal));
        Assert.DoesNotContain(hostileRole, log.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            model.RequestMessages,
            message => (message ?? string.Empty).Contains(hostileRole, StringComparison.Ordinal));

        // The correction was correctable: the loop carried on and finished the investigation.
        Assert.Equal(3, model.OrchestratorCalls);
        Assert.Equal(1, model.WorkerCalls);
        Assert.Single(harness.Reports.Published);
    }

    private static ProcessorHarness CreateHarness(
        IAiModelClient model,
        string orchestratorRouteId = "report-chat",
        string analysisRouteId = "analysis-chat",
        string? reportFailureMessage = null,
        int reportFailureCount = 1)
    {
        var configuration = CreateConfiguration(orchestratorRouteId, analysisRouteId);
        var ledgerWriter = new RecordingLedgerWriter();
        var ledgerReader = new StaticLedgerReader();
        var appender = new TriageLedgerAppender(ledgerWriter);
        var timeProvider = new ConstantTimeProvider(DateTimeOffset.UtcNow);
        var modelCaller = new InvestigationModelCaller(model, ledgerReader, appender, timeProvider);
        var artifacts = new RecordingArtifactRepository();
        var delegateExecutor = new AnalysisDelegateExecutor(
            artifacts,
            appender,
            ledgerReader,
            new WorkerRoleRunner(
                modelCaller,
                new WorkerToolCallExecutor([], new ToolRuleEngine(ledgerReader), appender, new UnusedToolResultCommitter(), timeProvider),
                appender),
            timeProvider);
        var reports = new RecordingReportRepository(reportFailureMessage, reportFailureCount);
        var logger = new RecordingLogger<GovernedTriageInvestigationProcessor>();
        var processor = new GovernedTriageInvestigationProcessor(
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
            logger);
        return new ProcessorHarness(processor, configuration, ledgerWriter, reports, logger, artifacts);
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
        RecordingReportRepository reports,
        RecordingLogger<GovernedTriageInvestigationProcessor> logger,
        RecordingArtifactRepository artifacts)
    {
        public RecordingLedgerWriter LedgerWriter { get; } = ledgerWriter;

        public RecordingReportRepository Reports { get; } = reports;

        public RecordingLogger<GovernedTriageInvestigationProcessor> Logger { get; } = logger;

        public RecordingArtifactRepository Artifacts { get; } = artifacts;

        public Task ProcessAsync() =>
            processor.ProcessAsync(CreateJob(), configuration, "worker-test", TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Answers an orchestrator turn (the request carries the orchestrator tool surface) with delegate
    /// first and publish_report second, and any worker turn with schema-valid output.
    /// </summary>
    /// <param name="workerOutput">The text every worker turn answers with.</param>
    /// <param name="refusedFirstDelegateRole">
    /// When set, the first orchestrator turn delegates to this role instead of <c>analysis</c>, and
    /// the real delegate turn moves to second place. It is how a turn the backend must refuse is
    /// scripted without changing what every other test here sees.
    /// </param>
    private sealed class ScriptedOrchestratorModel(
        string workerOutput = WorkerOutput,
        string? refusedFirstDelegateRole = null) : IAiModelClient
    {
        public int OrchestratorCalls { get; private set; }

        public int WorkerCalls { get; private set; }

        /// <summary>
        /// Every message of every request, captured at call time. The delegate result the orchestrator
        /// is given arrives as a tool message on its second turn, so this is where a worker's text
        /// becomes model input again.
        /// </summary>
        public List<string> RequestMessages { get; } = [];

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            RequestMessages.AddRange(request.Messages.Select(message => message.Content));
            if (request.Tools is not { Count: > 0 })
            {
                WorkerCalls++;
                return Task.FromResult(Response(workerOutput, []));
            }

            OrchestratorCalls++;
            if (refusedFirstDelegateRole is not null && OrchestratorCalls == 1)
            {
                return Task.FromResult(DelegateTurn(refusedFirstDelegateRole));
            }

            var delegateTurn = refusedFirstDelegateRole is null ? 1 : 2;
            return Task.FromResult(
                OrchestratorCalls == delegateTurn ? DelegateTurn("analysis") : PublishTurn());
        }

        private static AiModelResponse DelegateTurn(string role) =>
            Response("Delegate analysis.", [new AiToolCall(
                "call-delegate",
                OrchestratorToolNames.Delegate,
                "v1",
                Arguments(JsonSerializer.Serialize(new
                {
                    role,
                    task = "Analyze the checkout timeout."
                })))]);

        private static AiModelResponse PublishTurn() =>
            Response("Publish the report.", [new AiToolCall(
                "call-publish",
                OrchestratorToolNames.PublishReport,
                "v1",
                Arguments("""
                    {"report_json":{"status":"Completed","summary":"Delegated analysis completed.","classification":"SimpleKnownError","confidence":"Medium","documentationFit":"Missing","evidence":[{"referenceId":"artifact:trigger-signal"}],"limitations":[],"recommendedNextAction":"Review the checkout logs."}}
                    """))]);

        private static AiModelResponse Response(string content, IReadOnlyList<AiToolCall> toolCalls) =>
            new(content, "test-model", "test-provider", new AiModelUsage(1, 1, 2), "correlation", toolCalls);
    }

    private sealed class StaticInvestigationContextRepository : ITriageJobInvestigationContextRepository
    {
        public Task<TriageJobInvestigationContext> GetAsync(Guid jobId, int attempt, CancellationToken cancellationToken) =>
            Task.FromResult(CreateContext());
    }

    private sealed class RecordingReportRepository(string? failureMessage, int failureCount) : ITriageReportRepository
    {
        private int failuresRaised;

        public List<TriageReport> Published { get; } = [];

        public Task<Guid> PublishAsync(TriageJob job, string workerId, TriageReport report, CancellationToken cancellationToken)
        {
            if (failureMessage is not null && failuresRaised < failureCount)
            {
                failuresRaised++;
                throw new TriageReportValidationException(failureMessage);
            }

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
            Guid jobId,
            int attempt,
            IReadOnlyCollection<string> referenceIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CitedEvidenceRedaction>>([]);
    }

    private sealed class EmptyAttemptModelFallbackRepository : IAttemptModelFallbackRepository
    {
        public Task<IReadOnlyList<AttemptModelFallback>> ReadCurrentAttemptAsync(
            Guid jobId, int attempt, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AttemptModelFallback>>([]);
    }

    private sealed class RecordingArtifactRepository : ITriageArtifactRepository
    {
        public List<TriageArtifact> Inserted { get; } = [];

        public Task InsertAsync(TriageArtifact artifact, CancellationToken cancellationToken)
        {
            Inserted.Add(artifact);
            return Task.CompletedTask;
        }

        public Task ReplaceJobLevelAsync(TriageArtifact artifact, CancellationToken cancellationToken) =>
            Task.CompletedTask;
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
