using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.UnitTests;

/// <summary>
/// What schedules a remediation pass, what switches it on, and what a failing pass does to the thing
/// that scheduled it.
/// </summary>
/// <remarks>
/// The pass under test is the real runner over a real model caller, with only the model client
/// scripted, so a budget refusal and a provider failure are produced by the same code paths an
/// investigation gets rather than by a stub raising an exception the product does not raise.
/// </remarks>
public sealed class RemediationTriggerTests
{
    private const string ServiceName = "checkout-api";
    private const string ConfigHash = "config-hash";
    private static readonly Guid ReportId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// The switch in its "on" position: declared, allowed, and neither mode disabled. All four have
    /// to be true, which is exactly what an operator has to do to turn any external action on.
    /// </summary>
    [Fact]
    public async Task Select_EnqueuesAPassWhenTheConfigurationEnablesTheTool()
    {
        var selection = await CreateWorkflow(Enabled()).SelectAsync(
            "local", ReportId, Guid.NewGuid(), Guid.NewGuid(), 1, ConfigHash,
            ServiceName, "prod", "high", TestContext.Current.CancellationToken);

        Assert.True(selection.ShouldEnqueue);
        Assert.Null(selection.RouteId);
    }

    /// <summary>
    /// The switch in its "off" position, one reason at a time. Each of these is the whole switch on
    /// its own, so an operator who turns off any one of them turns the pass off.
    /// </summary>
    [Theory]
    [InlineData("not-declared")]
    [InlineData("not-allowed")]
    [InlineData("tool-mode-disabled")]
    [InlineData("global-mode-disabled")]
    [InlineData("target-mismatch")]
    public async Task Select_EnqueuesNothingWhenAnyPartOfTheSwitchIsOff(string variant)
    {
        var selection = await CreateWorkflow(Disabled(variant)).SelectAsync(
            "local", ReportId, Guid.NewGuid(), Guid.NewGuid(), 1, ConfigHash,
            ServiceName, "prod", "high", TestContext.Current.CancellationToken);

        Assert.False(selection.ShouldEnqueue);
    }

    /// <summary>
    /// The shipped default, read out of the file that ships rather than restated here. The tool is
    /// declared so an operator can find it, and it is off twice over: its own mode is disabled and
    /// it is not in the allowed list. Turning the pass on is a deliberate edit to both.
    /// </summary>
    [Fact]
    public void ShippedConfiguration_DeclaresThePassAndLeavesItDisabled()
    {
        var configuration = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryRootLocator.Find(), "config", "incidentcompass.config.json")))!.AsObject();

        var tool = configuration["Tools"]![RemediationDiffToolDescriptor.ToolId]!;
        Assert.Equal("external_action", tool["Kind"]!.GetValue<string>());
        Assert.Equal("code_write", tool["Category"]!.GetValue<string>());
        Assert.Equal(RemediationDiffToolDescriptor.LogicalTargetId, tool["LogicalTargetId"]!.GetValue<string>());
        Assert.Equal("disabled", tool["Mode"]!.GetValue<string>());
        Assert.Equal("disabled", configuration["Actions"]!["DefaultMode"]!.GetValue<string>());
        Assert.Empty(configuration["Actions"]!["AllowedTools"]!.AsArray());
    }

    /// <summary>
    /// The switch is read again when the intent is evaluated, not only when it was written. An
    /// operator who turns the pass off between publication and evaluation stops it before it spends
    /// anything, and the intent completes saying why rather than waiting for an attempt cap.
    /// </summary>
    [Fact]
    public async Task Evaluate_SpendsNothingWhenTheSwitchWasTurnedOffAfterTheIntentWasWritten()
    {
        var model = new ScriptedModelClient(_ => throw new InvalidOperationException("must not be called"));

        var result = await EvaluateAsync(Disabled("tool-mode-disabled"), model);

        Assert.True(result.IsCompleted);
        Assert.Equal(RemediationPostReportActionWorkflow.DisabledCode, result.Code);
        Assert.Equal(0, model.CallCount);
    }

    [Fact]
    public async Task Evaluate_DeadLettersAnIntentThatIsNotThisWorkflowsOwn()
    {
        var result = await CreateWorkflow(Enabled()).EvaluateAsync(
            CreateIntent() with { WorkflowVersion = 2 },
            TestContext.Current.CancellationToken);

        Assert.False(result.IsCompleted);
        Assert.False(result.ShouldRetry);
        Assert.Equal(RemediationPostReportActionWorkflow.IntentInvalidCode, result.Code);
    }

    [Fact]
    public async Task Evaluate_DeadLettersWhenTheReportIsNoLongerReadable()
    {
        var result = await EvaluateAsync(
            Enabled(),
            new ScriptedModelClient(_ => throw new InvalidOperationException("must not be called")),
            contextConfigured: false);

        Assert.False(result.IsCompleted);
        Assert.Equal(RemediationPostReportActionWorkflow.ReportUnavailableCode, result.Code);
    }

    /// <summary>
    /// A refusal is a settled outcome carried onto the intent, not a retry. Nothing about running
    /// the same pass again gives an unconfigured host a checkout.
    /// </summary>
    [Fact]
    public async Task Evaluate_CompletesCarryingTheRefusalCodeWhenNothingIsConfigured()
    {
        var result = await EvaluateAsync(
            Enabled(),
            new ScriptedModelClient(_ => throw new InvalidOperationException("must not be called")),
            workspace: new UnavailableRemediationWorkspace());

        Assert.True(result.IsCompleted);
        Assert.False(result.ShouldRetry);
        Assert.Equal(RemediationCodes.NotConfigured, result.Code);
    }

    /// <summary>
    /// A pass whose attempt has no budget left ends the intent and leaves the loop alone. It is
    /// dead-lettered rather than retried because a second pass would find the same spent budget.
    /// </summary>
    [Fact]
    public async Task Evaluate_DeadLettersInsteadOfThrowingWhenTheAttemptBudgetIsSpent()
    {
        var result = await EvaluateAsync(
            Enabled(),
            new ScriptedModelClient(_ => throw new InvalidOperationException("must not be called")),
            tokensAlreadySpent: 500_000);

        Assert.False(result.IsCompleted);
        Assert.False(result.ShouldRetry);
        Assert.Equal(RemediationPostReportActionWorkflow.BudgetExhaustedCode, result.Code);
    }

    /// <summary>
    /// A provider failure ends the intent with a closed code and, before it does, pays for the call
    /// that failed. The investigation path writes that accounting from its attempt-failure handler;
    /// a remediation pass has no such handler, so without this the roll-up would be missing exactly
    /// the spend that went wrong.
    /// </summary>
    [Fact]
    public async Task Evaluate_AccountsForAFailedModelCallAndDeadLetters()
    {
        var ledger = new RecordingLedgerWriter();

        var result = await EvaluateAsync(
            Enabled(),
            new ScriptedModelClient(_ => throw new AiModelException(
                "test-provider",
                "provider down",
                failureKind: ProviderFailureKind.TransportFailure)),
            ledger: ledger);

        Assert.False(result.IsCompleted);
        Assert.False(result.ShouldRetry);
        Assert.Equal(RemediationPostReportActionWorkflow.ModelCallFailedCode, result.Code);
        Assert.Contains(ledger.Requests, request => request.EventType == TriageLedgerEventType.ModelCall);
    }

    /// <summary>
    /// Every outcome this workflow can put on an intent has to be a code the evaluation pump will
    /// accept, or the pump turns it into a dead letter that says nothing about what happened.
    /// </summary>
    [Theory]
    [InlineData(RemediationPostReportActionWorkflow.IntentInvalidCode)]
    [InlineData(RemediationPostReportActionWorkflow.DisabledCode)]
    [InlineData(RemediationPostReportActionWorkflow.ReportUnavailableCode)]
    [InlineData(RemediationPostReportActionWorkflow.BudgetExhaustedCode)]
    [InlineData(RemediationPostReportActionWorkflow.ModelCallFailedCode)]
    [InlineData(RemediationPostReportActionWorkflow.AccountingPendingCode)]
    [InlineData(RemediationCodes.Produced)]
    [InlineData(RemediationCodes.NotConfigured)]
    [InlineData(RemediationCodes.ReleaseUnavailable)]
    [InlineData(RemediationCodes.SourceEvidenceMissing)]
    [InlineData(RemediationCodes.RouteMissing)]
    [InlineData(RemediationCodes.AnswerNotAPatch)]
    [InlineData(RemediationCodes.BaseMismatch)]
    public void EveryOutcomeCodeIsOneTheEvaluationPumpAccepts(string code)
    {
        Assert.InRange(code.Length, 1, 128);
        Assert.DoesNotContain("internal_", code, StringComparison.Ordinal);
        Assert.NotEqual("attempts_exhausted", code);
        Assert.All(code, character =>
            Assert.True(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_', code));
    }

    private static async Task<PostReportActionWorkflowResult> EvaluateAsync(
        TriageConfiguration configuration,
        ScriptedModelClient model,
        RemediationPassContext? context = null,
        IRemediationWorkspace? workspace = null,
        RecordingLedgerWriter? ledger = null,
        int tokensAlreadySpent = 0,
        bool contextConfigured = true)
    {
        var writer = ledger ?? new RecordingLedgerWriter();
        var services = new ServiceCollection();
        services.AddSingleton<ITriageLedgerWriter>(writer);
        services.AddSingleton(new TriageLedgerAppender(writer));
        services.AddSingleton<ITriageLedgerReader>(new StaticLedgerReader(tokensAlreadySpent));
        services.AddSingleton<IAiModelClient>(model);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<InvestigationModelCaller>();
        services.AddSingleton(workspace ?? new IdentifyingWorkspace());
        services.AddSingleton<IRemediationDiffRepository>(new DiscardingDiffRepository());
        services.AddSingleton<RemediationDiffRunner>();
        services.AddSingleton<IRemediationPassContextRepository>(
            new StaticPassContextRepository(contextConfigured ? context ?? CreateContext() : null));
        using var provider = services.BuildServiceProvider();

        return await CreateWorkflow(configuration, provider).EvaluateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);
    }

    private static RemediationPostReportActionWorkflow CreateWorkflow(
        TriageConfiguration configuration,
        IServiceProvider? provider = null) =>
        new(new StaticConfigurationRepository(configuration),
            (provider ?? new ServiceCollection().BuildServiceProvider())
                .GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System);

    private static PostReportActionIntent CreateIntent()
    {
        var input = Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(new JsonObject
        {
            ["originReportId"] = ReportId.ToString("N"),
            ["toolId"] = RemediationDiffToolDescriptor.ToolId,
            ["workflowVersion"] = 1
        }));
        return new PostReportActionIntent(
            Guid.NewGuid(), "local", ReportId, Guid.NewGuid(), Guid.NewGuid(), 1,
            RemediationDiffToolDescriptor.ToolId, 1, null, ConfigHash,
            $"post-report:v1:{ReportId:N}:{RemediationDiffToolDescriptor.ToolId}", input,
            PostReportActionIntentState.Processing, "worker", Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1), 1, null, null, DateTimeOffset.UtcNow, null);
    }

    private static RemediationPassContext CreateContext()
    {
        var faultId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var job = new TriageJob(
            Guid.NewGuid(), faultId, TriageJobStatus.Succeeded, 1, null, null, null, null, null,
            ConfigHash, now, now);
        var payload = JsonSerializer.SerializeToElement(new
        {
            evidenceKind = "SourceCode",
            relativePath = "src/Checkout.cs",
            lineStart = 1,
            lineEnd = 2,
            excerpt = "public static decimal Total(decimal p, int q) => p * q - 1;"
        });
        return new RemediationPassContext(
            job,
            new Fault(
                faultId, Guid.NewGuid(), "local", FaultStatus.Completed, "fingerprint", 1,
                FingerprintStrength.Strong, true, ServiceName, "prod", "high", null, now, now, null),
            new TriageReport(
                TriageReportStatus.Completed, "summary", "LikelyRegression", "High", [], [],
                "fix the total calculation"),
            [
                new TriageArtifact(
                    Guid.NewGuid(), job.Id, 1, ArtifactKind.RetrievedItem,
                    "source:1.4:src/Checkout.cs", payload, "hash", now)
            ]);
    }

    private static TriageConfiguration Enabled() => CreateConfiguration(
        toolMode: "live",
        globalMode: "live",
        allowed: true,
        declared: true,
        logicalTargetId: RemediationDiffToolDescriptor.LogicalTargetId);

    private static TriageConfiguration Disabled(string variant) => variant switch
    {
        "not-declared" => CreateConfiguration("live", "live", allowed: true, declared: false,
            RemediationDiffToolDescriptor.LogicalTargetId),
        "not-allowed" => CreateConfiguration("live", "live", allowed: false, declared: true,
            RemediationDiffToolDescriptor.LogicalTargetId),
        "tool-mode-disabled" => CreateConfiguration("disabled", "live", allowed: true, declared: true,
            RemediationDiffToolDescriptor.LogicalTargetId),
        "global-mode-disabled" => CreateConfiguration("live", "disabled", allowed: true, declared: true,
            RemediationDiffToolDescriptor.LogicalTargetId),
        _ => CreateConfiguration("live", "live", allowed: true, declared: true, "source:somewhere-else")
    };

    private static TriageConfiguration CreateConfiguration(
        string toolMode,
        string globalMode,
        bool allowed,
        bool declared,
        string logicalTargetId)
    {
        var tools = new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal);
        if (declared)
        {
            tools[RemediationDiffToolDescriptor.ToolId] = new TriageToolSettings(
                "external_action", null, null, null, "code_write", logicalTargetId, toolMode);
        }

        return new TriageConfiguration(
            ConfigHash,
            new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal)
            {
                ["mock"] = new("Mock", null, null)
            },
            new Dictionary<string, TriageRouteSettings>(StringComparer.Ordinal)
            {
                ["report-chat"] = new("Chat", "mock", "test-model", 0, 4000, 200000)
            },
            new OrchestratorSettings(
                "orchestrator instructions",
                "report-chat",
                ["delegate", "publish_report"],
                new OrchestratorBudgetSettings(2, 100000, 600, 2)),
            new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal),
            tools,
            [],
            new IngestionSettings("local", ["tester"]),
            new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
            RedactionSettings.Default)
        {
            CurrentReleases = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ServiceName] = "1.4"
            },
            Actions = new TriageActionSettings(
                allowed ? [RemediationDiffToolDescriptor.ToolId] : [],
                globalMode,
                RequireApprovalForAll: false,
                ApprovalTtlMinutes: 60)
        };
    }

    private sealed class StaticConfigurationRepository(TriageConfiguration configuration)
        : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(configuration);

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
            Task.FromResult(configuration);
    }

    private sealed class StaticPassContextRepository(RemediationPassContext? context)
        : IRemediationPassContextRepository
    {
        public Task<RemediationPassContext?> FindAsync(
            string tenantId,
            Guid reportId,
            CancellationToken cancellationToken) => Task.FromResult(context);
    }

    private sealed class IdentifyingWorkspace : IRemediationWorkspace
    {
        public Task<RemediationBaseResult> IdentifyBaseAsync(
            RemediationTarget target,
            CancellationToken cancellationToken) =>
            Task.FromResult(RemediationBaseResult.Identified("base-tree-identity"));

        public Task<RemediationApplyResult> ApplyAsync(
            RemediationApplyRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(RemediationApplyResult.Applied("result-tree-identity", 1));
    }

    private sealed class DiscardingDiffRepository : IRemediationDiffRepository
    {
        public Task AddAsync(RemediationDiff diff, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ScriptedModelClient(Func<AiModelRequest, AiModelResponse> answer) : IAiModelClient
    {
        public int CallCount { get; private set; }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(answer(request));
        }
    }

    private sealed class StaticLedgerReader(int tokensSpent) : ITriageLedgerReader
    {
        public Task<TriageBudgetLedgerUsage> ReadBudgetUsageAsync(
            TriageJob job,
            CancellationToken cancellationToken) =>
            Task.FromResult(new TriageBudgetLedgerUsage(tokensSpent, 0));

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
