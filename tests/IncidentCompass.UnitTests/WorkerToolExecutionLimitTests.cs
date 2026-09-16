using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// An immediate worker tool runs under its own execution limit, the attempt duration ceiling and the
/// host token, and each way it can end leaves a distinct, accurate outcome. The tool here awaits its
/// token, and time moves only when a test advances <see cref="ManualTimerTimeProvider"/>, so every
/// timeout fires exactly when the test says and nothing waits on the wall clock.
/// </summary>
public sealed class WorkerToolExecutionLimitTests
{
    private const string ToolName = "slow_probe";

    private const string OutputSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": { "keyFacts": { "type": "array", "items": { "type": "string" } } },
          "required": ["keyFacts"]
        }
        """;

    [Fact]
    public async Task ExecuteAsync_PerToolLimitFires_RecordsTimeoutAndReturnsTheToolFailureMessage()
    {
        var harness = new Harness(toolTimeoutSeconds: 5);

        var execution = harness.ExecuteAsync(CancellationToken.None);
        await harness.Tool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        var output = await execution;

        using var document = JsonDocument.Parse(output);
        Assert.Equal("Failed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(WorkerToolExecutionLimiter.TimeoutErrorCode, document.RootElement.GetProperty("errorCode").GetString());
        var result = Assert.Single(harness.ToolResults());
        Assert.Equal(TriageLedgerToolStatus.Failed, result.ToolStatus);
        Assert.Equal(ToolName, result.ToolName);
        Assert.Contains(WorkerToolExecutionLimiter.TimeoutErrorCode, result.Rationale, StringComparison.Ordinal);
        Assert.Contains("5-second", result.Rationale, StringComparison.Ordinal);
        Assert.Contains(harness.Logger.Entries, entry => entry.EventId.Id == 3305);
        Assert.DoesNotContain(harness.Writer.Requests, request => request.EventType == TriageLedgerEventType.BudgetEvent);
    }

    [Fact]
    public async Task RunAsync_PerToolLimitFires_WorkerContinuesWithTheFailureAndCompletes()
    {
        var harness = new Harness(toolTimeoutSeconds: 5);
        var model = new ToolThenOutputModel();
        var runner = new WorkerRoleRunner(
            new InvestigationModelCaller(model, harness.Reader, harness.Appender, harness.Time),
            harness.Executor,
            harness.Appender);

        var run = runner.RunAsync(
            harness.Job,
            harness.Configuration,
            Harness.InvestigationContext(),
            "analysis",
            harness.Configuration.Roles["analysis"],
            "Look into the timeout.",
            harness.Time.GetUtcNow(),
            InvestigationProgressTracker.For(harness.Configuration.Orchestrator.Budget),
            TestContext.Current.CancellationToken);
        await harness.Tool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        var output = await run;

        Assert.Contains("keyFacts", output, StringComparison.Ordinal);
        Assert.Equal(2, model.Calls);
        Assert.Contains(WorkerToolExecutionLimiter.TimeoutErrorCode, model.LastToolMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_AttemptCeilingFiresDuringTheTool_EndsWithTheWallClockBudgetCode()
    {
        var harness = new Harness(toolTimeoutSeconds: 600, attemptDurationSeconds: 60);

        var execution = harness.ExecuteAsync(CancellationToken.None);
        await harness.Tool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(60));

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => execution);
        Assert.Equal(TriageBudgetExhaustedException.WallClockReachedDuringCallCode, exception.ErrorCode);
        var budgetEvent = Assert.Single(
            harness.Writer.Requests, request => request.EventType == TriageLedgerEventType.BudgetEvent);
        Assert.StartsWith("wall_clock_limit_reached:", budgetEvent.Rationale, StringComparison.Ordinal);
        Assert.Empty(harness.ToolResults());
        Assert.Contains(harness.Logger.Entries, entry => entry.EventId.Id == 3306);
    }

    [Fact]
    public async Task ExecuteAsync_HostCancellation_PropagatesWithoutAToolResult()
    {
        var harness = new Harness(toolTimeoutSeconds: 5, attemptDurationSeconds: 60);
        using var host = new CancellationTokenSource();

        var execution = harness.ExecuteAsync(host.Token);
        await harness.Tool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await host.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Empty(harness.ToolResults());
        Assert.DoesNotContain(harness.Writer.Requests, request => request.EventType == TriageLedgerEventType.BudgetEvent);
    }

    [Fact]
    public async Task ExecuteAsync_ToolThrows_RecordsTheFailureNamingTheToolAndRethrowsTheSameException()
    {
        var thrown = new InvalidOperationException("connector detail that must not be logged");
        var harness = new Harness(toolTimeoutSeconds: null, throwOnExecute: thrown);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.ExecuteAsync(CancellationToken.None));

        Assert.Same(thrown, exception);
        var result = Assert.Single(harness.ToolResults());
        Assert.Equal(TriageLedgerToolStatus.Failed, result.ToolStatus);
        Assert.Equal(ToolName, result.ToolName);
        Assert.Contains(WorkerToolExecutionLimiter.FailedErrorCode, result.Rationale, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), result.Rationale, StringComparison.Ordinal);
        Assert.DoesNotContain("connector detail", result.Rationale, StringComparison.Ordinal);
        var log = harness.Logger.Single(3307);
        Assert.DoesNotContain("connector detail", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutUnset_DefaultsToOneHundredTwentySeconds()
    {
        var harness = new Harness(toolTimeoutSeconds: null);

        var execution = harness.ExecuteAsync(CancellationToken.None);
        await harness.Tool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(119));
        Assert.False(execution.IsCompleted);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var output = await execution;

        Assert.Contains(WorkerToolExecutionLimiter.TimeoutErrorCode, output, StringComparison.Ordinal);
        Assert.Contains("120-second", Assert.Single(harness.ToolResults()).Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ToolIgnoresCancellation_StopsWaitingAtTheLimitAndAbandonsTheTool()
    {
        var harness = new Harness(toolTimeoutSeconds: 5);
        harness.Tool.Behavior = SlowToolBehavior.IgnoresCancellation;

        var execution = harness.ExecuteAsync(CancellationToken.None);
        await harness.Tool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        var output = await execution.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains(WorkerToolExecutionLimiter.TimeoutErrorCode, output, StringComparison.Ordinal);
        Assert.False(harness.Tool.Release.Task.IsCompleted);
        Assert.Single(harness.ToolResults());

        // The abandoned tool finishes later with an exception; it is observed, and nothing more is
        // written for a call that already has its outcome.
        harness.Tool.Release.SetResult();
        Assert.Single(harness.ToolResults());
    }

    [Fact]
    public async Task ExecuteAsync_ToolConvertsCancellationWhenItsLimitFires_IsStillATimeout()
    {
        var harness = new Harness(toolTimeoutSeconds: 5);
        harness.Tool.Behavior = SlowToolBehavior.ConvertsCancellation;

        var execution = harness.ExecuteAsync(CancellationToken.None);
        await harness.Tool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(5));
        var output = await execution;

        Assert.Contains(WorkerToolExecutionLimiter.TimeoutErrorCode, output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            harness.ToolResults(),
            result => result.Rationale!.Contains(WorkerToolExecutionLimiter.FailedErrorCode, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_ToolConvertsCancellationWhenTheCeilingFires_IsTheWallClockCode()
    {
        var harness = new Harness(toolTimeoutSeconds: 600, attemptDurationSeconds: 60);
        harness.Tool.Behavior = SlowToolBehavior.ConvertsCancellation;

        var execution = harness.ExecuteAsync(CancellationToken.None);
        await harness.Tool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(60));

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => execution);
        Assert.Equal(TriageBudgetExhaustedException.WallClockReachedDuringCallCode, exception.ErrorCode);
        Assert.Empty(harness.ToolResults());
    }

    [Fact]
    public async Task ExecuteAsync_ToolConvertsHostCancellation_PropagatesCancellationWithoutALedgerWrite()
    {
        var harness = new Harness(toolTimeoutSeconds: 5);
        harness.Tool.Behavior = SlowToolBehavior.ConvertsCancellation;
        using var host = new CancellationTokenSource();

        var execution = harness.ExecuteAsync(host.Token);
        await harness.Tool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var writesBefore = harness.Writer.Requests.Count;
        await host.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.Equal(writesBefore, harness.Writer.Requests.Count);
    }

    [Fact]
    public async Task ExecuteAsync_BudgetEventCannotBeRecorded_StillEndsWithTheWallClockCode()
    {
        var harness = new Harness(toolTimeoutSeconds: 600, attemptDurationSeconds: 60);
        harness.Writer.FailBudgetEvents = true;

        var execution = harness.ExecuteAsync(CancellationToken.None);
        await harness.Tool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(60));

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => execution);
        Assert.Equal(TriageBudgetExhaustedException.WallClockReachedDuringCallCode, exception.ErrorCode);
        Assert.Contains(harness.Logger.Entries, entry => entry.EventId.Id == 3309);
    }

    [Fact]
    public async Task ExecuteAsync_ToolLimitAndCeilingFireInTheSameInstant_TheCeilingWins()
    {
        // Both bounds are 60 seconds away, so one Advance fires both timers. The ceiling ends the
        // attempt, which outranks recording a per-tool timeout the worker could have continued past.
        var harness = new Harness(toolTimeoutSeconds: 60, attemptDurationSeconds: 60);

        var execution = harness.ExecuteAsync(CancellationToken.None);
        await harness.Tool.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        harness.Time.Advance(TimeSpan.FromSeconds(60));

        var exception = await Assert.ThrowsAsync<TriageBudgetExhaustedException>(() => execution);
        Assert.Equal(TriageBudgetExhaustedException.WallClockReachedDuringCallCode, exception.ErrorCode);
        Assert.Empty(harness.ToolResults());
    }

    private sealed class Harness
    {
        public Harness(int? toolTimeoutSeconds, int? attemptDurationSeconds = null, Exception? throwOnExecute = null)
        {
            Tool = new SlowTool(throwOnExecute);
            Appender = new TriageLedgerAppender(Writer);
            Executor = new WorkerToolCallExecutor(
                [Tool], new ToolRuleEngine(Reader), Appender, new UnusedCommitter(), Time, logger: Logger);
            Configuration = TestTriageConfiguration.Create() with
            {
                Orchestrator = new OrchestratorSettings(
                    "orchestrator instructions",
                    "report-chat",
                    ["delegate", "publish_report"],
                    new OrchestratorBudgetSettings(
                        MaxWorkers: 4, MaxTokens: 100_000, MaxAttemptDurationSeconds: attemptDurationSeconds)),
                Roles = new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal)
                {
                    ["analysis"] = new("analysis-chat", "analysis instructions", [ToolName], OutputSchema)
                },
                Tools = new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal)
                {
                    [ToolName] = new("internal", null, null, null, TimeoutSeconds: toolTimeoutSeconds)
                },
                Rules = []
            };
            var now = Time.GetUtcNow();
            Job = new TriageJob(
                Guid.NewGuid(), Guid.NewGuid(), TriageJobStatus.Processing, 1, "worker-test",
                now.AddMinutes(5), null, null, null, "config-hash", now, now);
        }

        public ManualTimerTimeProvider Time { get; } = new();

        public RecordingLedgerWriter Writer { get; } = new();

        public ZeroLedgerReader Reader { get; } = new();

        public RecordingLogger<WorkerToolCallExecutor> Logger { get; } = new();

        public SlowTool Tool { get; }

        public TriageLedgerAppender Appender { get; }

        public WorkerToolCallExecutor Executor { get; }

        public TriageConfiguration Configuration { get; }

        public TriageJob Job { get; }

        public Task<string> ExecuteAsync(CancellationToken cancellationToken) =>
            Executor.ExecuteAsync(
                Job,
                Configuration,
                InvestigationContext(),
                "analysis",
                new AiToolCall("call-1", ToolName, "v1", EmptyObject()),
                Time.GetUtcNow(),
                InvestigationProgressTracker.For(Configuration.Orchestrator.Budget),
                cancellationToken);

        public TriageLedgerAppendRequest[] ToolResults() =>
            Writer.Requests.Where(request => request.EventType == TriageLedgerEventType.ToolResult).ToArray();

        public static TriageJobInvestigationContext InvestigationContext()
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
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private sealed class SlowTool(Exception? throwOnExecute) : IImmediateAgentTool
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SlowToolBehavior Behavior { get; set; }

        public AiToolDefinition Definition { get; } = new(ToolName, "Slow probe tool.", "v1", EmptyObject());

        public ToolValidationResult Validate(JsonElement arguments) => ToolValidationResult.Valid(arguments);

        public async Task<ToolExecutionResult> ExecuteAsync(
            AgentToolExecutionContext context,
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            if (throwOnExecute is not null)
            {
                throw throwOnExecute;
            }

            Started.TrySetResult();
            switch (Behavior)
            {
                case SlowToolBehavior.IgnoresCancellation:
                    await Release.Task;
                    throw new InvalidOperationException("The abandoned tool failed after it was released.");
                case SlowToolBehavior.ConvertsCancellation:
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new InvalidOperationException("The tool turned its cancellation into another failure.");
                    }

                    break;
                default:
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    break;
            }

            throw new InvalidOperationException("The slow probe tool completed without being cancelled.");
        }
    }

    private enum SlowToolBehavior
    {
        ObservesCancellation,
        IgnoresCancellation,
        ConvertsCancellation
    }

    private sealed class ToolThenOutputModel : IAiModelClient
    {
        public int Calls { get; private set; }

        public string LastToolMessage { get; private set; } = string.Empty;

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                return Task.FromResult(Response(string.Empty, [new AiToolCall("call-1", ToolName, "v1", EmptyObject())]));
            }

            LastToolMessage = request.Messages.Last(message => message.Role == AiMessageRole.Tool).Content;
            return Task.FromResult(Response("""{"keyFacts":["The source lookup timed out."]}""", []));
        }

        private static AiModelResponse Response(string content, IReadOnlyList<AiToolCall> toolCalls) =>
            new(content, "test-model", "test-provider", new AiModelUsage(1, 1, 2), "correlation", toolCalls);
    }

    private sealed class UnusedCommitter : ITriageToolResultCommitter
    {
        public Task<TriageArtifact> CommitSucceededAsync(
            TriageToolResultCommitRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ZeroLedgerReader : ITriageLedgerReader
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

        public bool FailBudgetEvents { get; set; }

        public Task<TriageLedgerEntry> AppendAsync(TriageLedgerAppendRequest request, CancellationToken cancellationToken)
        {
            if (FailBudgetEvents && request.EventType == TriageLedgerEventType.BudgetEvent)
            {
                throw new InvalidOperationException("Synthetic ledger outage.");
            }

            lock (Requests)
            {
                Requests.Add(request);
            }

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
