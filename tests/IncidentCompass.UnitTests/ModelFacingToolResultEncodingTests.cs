using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using static IncidentCompass.UnitTests.ModelFacingJsonText;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The two documents a model reads mid-run - a worker tool's result and a delegate's answer - carry
/// non-Latin text as text. The durable side of the same call is untouched: the committed element and
/// its content hash are the canonical form they always were.
/// </summary>
public sealed class ModelFacingToolResultEncodingTests
{
    private const string ToolName = "memory_search";

    private const string Quote = "Тайм-аут оформления заказа обычно означает задержку платёжного шлюза.";

    private const string Title = "Регламент: тайм-аут оформления заказа";

    /// <summary>The first two hex digits of every Cyrillic escape the old encoding produced.</summary>
    private static readonly string CyrillicEscapePrefix = Backslash + "u04";

    [Fact]
    public async Task ASucceededToolResult_ReachesTheModelReadable()
    {
        var harness = new Harness(ToolResult(Quote));

        var message = await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Contains(Quote, message, StringComparison.Ordinal);
        Assert.Contains(Title, message, StringComparison.Ordinal);
        Assert.DoesNotContain(CyrillicEscapePrefix, message, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(message);
        Assert.Equal(Quote, document.RootElement.GetProperty("items")[0].GetProperty("quote").GetString());
    }

    /// <summary>
    /// The committed document and its hash are the canonical form, which is what the report, the
    /// evidence identity and the stored row all read. Only the message text changed.
    /// </summary>
    [Fact]
    public async Task TheCommittedDocumentAndItsHash_AreUnchangedByTheModelFacingForm()
    {
        var output = ToolResult(Quote);
        var harness = new Harness(output);

        await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        var committed = Assert.Single(harness.Committer.Requests);
        Assert.Equal(output.GetRawText(), committed.Output.GetRawText());
        Assert.Equal(
            CanonicalJsonSerializer.ComputeSha256Hex(
                CanonicalJsonSerializer.Canonicalize(JsonNode.Parse(output.GetRawText()))),
            committed.ContentHash);
        Assert.Contains(CyrillicEscapePrefix, committed.Output.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedToolResult_CarriesItsReasonReadable()
    {
        var harness = new Harness(
            EmptyObject(),
            new ToolExecutionResult(
                ToolExecutionStatus.Failed,
                EmptyObject(),
                "memory_unavailable",
                "Хранилище памяти недоступно."));

        var message = await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(message);
        Assert.Equal("Хранилище памяти недоступно.", document.RootElement.GetProperty("errorMessage").GetString());
        Assert.DoesNotContain(CyrillicEscapePrefix, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A tool result is one line of the conversation, so the same boundary obligation applies: a
    /// quote carrying a newline, the end marker and raw invisible characters reaches the model as one
    /// quoted value, and the value the model parses is byte for byte the one the tool produced.
    /// </summary>
    [Fact]
    public async Task AToolResultCarryingHostileText_ReachesTheModelOnOneLine()
    {
        var hostile = HostileValue(TriageInvestigationPromptBuilder.UntrustedContextEndMarker);
        var harness = new Harness(ToolResult(hostile));

        var message = await harness.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.Single(Lines(message));
        Assert.Empty(LinesOpeningWithMarker(message, TriageInvestigationPromptBuilder.UntrustedContextEndMarker));
        Assert.Empty(RawInvisibleCharacters(message));
        using var document = JsonDocument.Parse(message);
        Assert.Equal(hostile, document.RootElement.GetProperty("items")[0].GetProperty("quote").GetString());
    }

    [Fact]
    public void AMemoryDelegateResult_ReachesTheOrchestratorReadable()
    {
        var workerOutput = CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["matched"] = true,
            ["rationale"] = "Найден подходящий регламент.",
            ["items"] = new JsonArray(new JsonObject
            {
                ["artifactId"] = "3f7e4b89-6d64-49dc-bb7e-0e9a5c7bde10",
                ["title"] = Title,
                ["quote"] = Quote,
                ["score"] = 0.92,
                ["documentationStatus"] = "Current"
            })
        }).GetRawText();

        var result = WorkerDelegateResultFactory.Create("memory", workerOutput, Guid.NewGuid());

        Assert.Contains(Quote, result.SerializedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(CyrillicEscapePrefix, result.SerializedPayload, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.SerializedPayload);
        var item = document.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(Title, item.GetProperty("title").GetString());
    }

    [Fact]
    public void AnAnalysisDelegateResult_ReachesTheOrchestratorReadable()
    {
        var workerOutput = CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["rationale"] = "Платёжный шлюз отвечал дольше таймаута.",
            ["keyFacts"] = new JsonArray("Задержка выросла после смены пула соединений."),
            ["candidateClassification"] = "KnownIncident",
            ["needsDeeperContext"] = false
        }).GetRawText();

        var result = WorkerDelegateResultFactory.Create("analysis", workerOutput, Guid.NewGuid());

        Assert.Contains("Платёжный шлюз отвечал дольше таймаута.", result.SerializedPayload, StringComparison.Ordinal);
        Assert.Contains("Задержка выросла после смены пула соединений.", result.SerializedPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(CyrillicEscapePrefix, result.SerializedPayload, StringComparison.Ordinal);
    }

    private static JsonElement ToolResult(string quote) => CanonicalJsonSerializer.ToElement(new JsonObject
    {
        ["matched"] = true,
        ["message"] = "matches found",
        ["items"] = new JsonArray(new JsonObject
        {
            ["artifactId"] = "3f7e4b89-6d64-49dc-bb7e-0e9a5c7bde10",
            ["title"] = Title,
            ["quote"] = quote,
            ["score"] = 0.92
        })
    });

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private sealed class Harness
    {
        public Harness(JsonElement output, ToolExecutionResult? result = null)
        {
            Configuration = TestTriageConfiguration.Create();
            var appender = new TriageLedgerAppender(new DiscardingLedgerWriter());
            Executor = new WorkerToolCallExecutor(
                [new StubTool(result ?? new ToolExecutionResult(ToolExecutionStatus.Succeeded, output))],
                new ToolRuleEngine(new PermissiveLedgerReader()),
                appender,
                Committer,
                TimeProvider.System);
            var now = DateTimeOffset.UnixEpoch;
            Job = new TriageJob(
                Guid.Parse("80000000-0000-0000-0000-000000000001"),
                Guid.Parse("80000000-0000-0000-0000-000000000002"),
                TriageJobStatus.Processing, 1, "worker-test", now.AddMinutes(5),
                null, null, null, Configuration.ConfigHash, now, now);
        }

        public RecordingCommitter Committer { get; } = new();

        public TriageConfiguration Configuration { get; }

        public WorkerToolCallExecutor Executor { get; }

        public TriageJob Job { get; }

        public Task<string> ExecuteAsync(CancellationToken cancellationToken) =>
            Executor.ExecuteAsync(
                Job,
                Configuration,
                InvestigationContext(),
                "memory",
                new AiToolCall("call-1", ToolName, "v1", EmptyObject()),
                TimeProvider.System.GetUtcNow(),
                InvestigationProgressTracker.For(Configuration.Orchestrator.Budget),
                cancellationToken);

        private static TriageJobInvestigationContext InvestigationContext()
        {
            var now = DateTimeOffset.UnixEpoch;
            var faultId = Guid.Parse("80000000-0000-0000-0000-000000000003");
            var signal = new Signal(
                Guid.Parse("80000000-0000-0000-0000-000000000004"), "tenant-a", "tester", faultId,
                "fingerprint", 1, FingerprintStrength.Strong, true, null, false, null, null, null, null, null,
                "checkout-api", "production", null, "Error", "TimeoutException", "Checkout timed out",
                "summary", null, null, null, null, null, EmptyObject(), EmptyObject(), now, now, null);
            var fault = new Fault(
                faultId, signal.Id, "tenant-a", FaultStatus.Analyzing, "fingerprint", 1,
                FingerprintStrength.Strong, true, "checkout-api", "production", "Error", null, now, null, null);
            return new TriageJobInvestigationContext(fault, signal, []);
        }
    }

    private sealed class StubTool(ToolExecutionResult result) : IImmediateAgentTool
    {
        public AiToolDefinition Definition { get; } = new(ToolName, "Stub memory search.", "v1", EmptyObject());

        public ToolValidationResult Validate(JsonElement arguments) => ToolValidationResult.Valid(arguments);

        public Task<ToolExecutionResult> ExecuteAsync(
            AgentToolExecutionContext context,
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class RecordingCommitter : ITriageToolResultCommitter
    {
        public List<TriageToolResultCommitRequest> Requests { get; } = [];

        public Task<TriageArtifact> CommitSucceededAsync(
            TriageToolResultCommitRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TriageArtifact(
                Guid.NewGuid(), request.Job.Id, request.Job.Attempt, ArtifactKind.ToolResult,
                "tool:" + request.ToolName, request.Output, request.ContentHash, DateTimeOffset.UnixEpoch));
        }
    }

    private sealed class DiscardingLedgerWriter : ITriageLedgerWriter
    {
        private long nextId;

        public Task<TriageLedgerEntry> AppendAsync(
            TriageLedgerAppendRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TriageLedgerEntry(
                ++nextId, request.FaultId, request.JobId, request.Attempt, request.EventType,
                request.Role, request.ToolName, request.Rationale, request.Decision, request.DecisionReason,
                request.PayloadRef, request.ConfigHash, DateTimeOffset.UnixEpoch, request.ToolStatus,
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

    private sealed class PermissiveLedgerReader : ITriageLedgerReader
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
}
