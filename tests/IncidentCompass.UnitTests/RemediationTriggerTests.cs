using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.ActionApprovals.Propose;
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
    /// The shipped default, read out of the file that ships rather than restated here. Both halves of
    /// the feature are declared so an operator can find them, and both are off twice over: each has
    /// its own mode disabled and neither is in the allowed list. Turning either on is a deliberate
    /// edit, and turning on the pass does not turn on the approval it can be frozen into.
    /// </summary>
    [Theory]
    [InlineData(RemediationDiffToolDescriptor.ToolId)]
    [InlineData(RemediationApplyToolDescriptor.ToolId)]
    public void ShippedConfiguration_DeclaresBothHalvesAndLeavesThemDisabled(string toolId)
    {
        var configuration = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryRootLocator.Find(), "config", "incidentcompass.config.json")))!.AsObject();

        var tool = configuration["Tools"]![toolId]!;
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
    /// The whole point of the feature, in one evaluation: a pass runs, records a diff, and the diff
    /// is frozen into one proposal a person has to approve.
    /// </summary>
    /// <remarks>
    /// The proposal is asserted on the command the workflow dispatched rather than on a stub's
    /// return, so what is checked is what the approval machinery would have received: the backend
    /// tool id, the report-scoped idempotency key and arguments whose diff bytes are the recorded
    /// ones.
    /// </remarks>
    [Fact]
    public async Task Evaluate_FreezesTheProducedDiffIntoOneRequestedProposal()
    {
        var diffs = new RecordingDiffRepository();
        var dispatcher = new RecordingDispatcher();
        using var provider = BuildProvider(
            PatchAnsweringModel(), diffRepository: diffs, dispatcher: dispatcher);

        var result = await CreateWorkflow(Enabled(), provider).EvaluateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);

        Assert.True(result.IsCompleted);
        Assert.Equal(RemediationProposalCodes.ProposalRequested, result.Code);
        var recorded = Assert.Single(diffs.Added);
        var proposal = Assert.Single(dispatcher.Proposals);
        Assert.Equal(RemediationApplyToolDescriptor.ToolId, proposal.ToolId);
        Assert.Equal($"post-report:v1:{ReportId:N}:remediation_apply", proposal.ProposalKey);
        Assert.Equal(recorded.PatchText, proposal.Arguments.GetProperty("patch").GetString());
        Assert.Equal(IdentifyingWorkspace.BaseIdentity, proposal.Arguments.GetProperty("baseTreeIdentity").GetString());
        Assert.Equal("not_executed", proposal.Arguments.GetProperty("testOutcome").GetString());
    }

    /// <summary>
    /// A second evaluation of the same intent spends nothing and creates nothing new.
    /// </summary>
    /// <remarks>
    /// This is the idempotency the item asks for, and it is bought by ordering rather than by
    /// deduplication after the fact: the proposal step runs first, finds the diff the first pass
    /// recorded, and proposes from it, so the model is never asked a second time and no second row
    /// can exist to make durable state ambiguous. The proposal key and every argument are identical,
    /// which is what makes the second dispatch a replay of one proposal rather than a second one.
    /// </remarks>
    [Fact]
    public async Task Evaluate_RunsNoSecondPassAndProposesNothingNewWhenADiffIsAlreadyRecorded()
    {
        var diffs = new RecordingDiffRepository();
        var dispatcher = new RecordingDispatcher();
        var model = PatchAnsweringModel();
        using var provider = BuildProvider(model, diffRepository: diffs, dispatcher: dispatcher);
        var workflow = CreateWorkflow(Enabled(), provider);

        var first = await workflow.EvaluateAsync(CreateIntent(), TestContext.Current.CancellationToken);
        var callsAfterFirst = model.CallCount;
        var second = await workflow.EvaluateAsync(CreateIntent(), TestContext.Current.CancellationToken);

        Assert.Equal(RemediationProposalCodes.ProposalRequested, first.Code);
        Assert.Equal(RemediationProposalCodes.ProposalRequested, second.Code);
        Assert.Equal(1, callsAfterFirst);
        Assert.Equal(callsAfterFirst, model.CallCount);
        Assert.Single(diffs.Added);
        Assert.Equal(2, dispatcher.Proposals.Count);
        Assert.Equal(dispatcher.Proposals[0].ProposalKey, dispatcher.Proposals[1].ProposalKey);
        Assert.Equal(
            dispatcher.Proposals[0].Arguments.GetRawText(),
            dispatcher.Proposals[1].Arguments.GetRawText());
    }

    /// <summary>
    /// Two recorded diffs for one report name no single change, so nothing becomes approvable.
    /// </summary>
    /// <remarks>
    /// The workflow cannot reach this state on its own, which is the point of the ordering above;
    /// the table is append-only and writable by a future release, so the read still has to answer
    /// honestly when it holds two rows rather than picking the newest and calling it the fix.
    /// </remarks>
    [Fact]
    public async Task Evaluate_ProposesNothingWhenTheReportHasMoreThanOneRecordedDiff()
    {
        var context = CreateContext();
        var diffs = new RecordingDiffRepository();
        diffs.Added.Add(Recorded(context, "@@ -1,1 +1,1 @@\n-a\n+b\n"));
        diffs.Added.Add(Recorded(context, "@@ -1,1 +1,1 @@\n-a\n+c\n"));
        var dispatcher = new RecordingDispatcher();
        var model = PatchAnsweringModel();
        using var provider = BuildProvider(
            model, context, diffRepository: diffs, dispatcher: dispatcher);

        var result = await CreateWorkflow(Enabled(), provider).EvaluateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);

        Assert.True(result.IsCompleted);
        Assert.Equal(RemediationProposalCodes.DiffAmbiguous, result.Code);
        Assert.Empty(dispatcher.Proposals);
        Assert.Equal(0, model.CallCount);
    }

    /// <summary>
    /// A diff prepared against a tree the checkout no longer holds creates no approvable proposal.
    /// </summary>
    [Fact]
    public async Task Evaluate_ProposesNothingWhenTheCheckoutMovedUnderTheRecordedDiff()
    {
        var context = CreateContext();
        var diffs = new RecordingDiffRepository();
        diffs.Added.Add(Recorded(context, "@@ -1,1 +1,1 @@\n-a\n+b\n") with
        {
            BaseTreeIdentity = new string('9', 64)
        });
        var dispatcher = new RecordingDispatcher();
        using var provider = BuildProvider(
            PatchAnsweringModel(), context, diffRepository: diffs, dispatcher: dispatcher);

        var result = await CreateWorkflow(Enabled(), provider).EvaluateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);

        Assert.Equal(RemediationProposalCodes.BaseStale, result.Code);
        Assert.Empty(dispatcher.Proposals);
    }

    /// <summary>
    /// A diff belonging to another attempt of the same report is refused, and a diff that claims a
    /// test ran is refused. Neither is something this release can honestly freeze.
    /// </summary>
    [Theory]
    [InlineData("other-attempt", RemediationProposalCodes.DiffForeign)]
    [InlineData("other-service", RemediationProposalCodes.DiffForeign)]
    [InlineData("claims-a-test", RemediationProposalCodes.DiffUnsupported)]
    public async Task Evaluate_ProposesNothingForADiffThatIsNotThisOrigins(string variant, string expected)
    {
        var context = CreateContext();
        var recorded = Recorded(context, "@@ -1,1 +1,1 @@\n-a\n+b\n");
        var diffs = new RecordingDiffRepository();
        diffs.Added.Add(variant switch
        {
            "other-attempt" => recorded with { Attempt = recorded.Attempt + 1 },
            "other-service" => recorded with { ServiceName = "another-service" },
            _ => recorded with { TestOutcome = "passed", TestCommandId = "dotnet-test" }
        });
        var dispatcher = new RecordingDispatcher();
        using var provider = BuildProvider(
            PatchAnsweringModel(), context, diffRepository: diffs, dispatcher: dispatcher);

        var result = await CreateWorkflow(Enabled(), provider).EvaluateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Code);
        Assert.Empty(dispatcher.Proposals);
    }

    /// <summary>
    /// A recorded diff whose report cites no source evidence is not grounded in anything, so it is
    /// refused rather than frozen.
    /// </summary>
    /// <remarks>
    /// The pass itself already refuses before calling a model when a report cites no source, so this
    /// is the same rule applied where it matters for an approval: what a person approves has to name
    /// the files the investigation actually read, and the frozen payload carries a digest over
    /// exactly that set.
    /// </remarks>
    [Fact]
    public async Task Evaluate_ProposesNothingWhenTheReportCitesNoSourceEvidence()
    {
        var context = CreateContext() with { SourceEvidence = [] };
        var diffs = new RecordingDiffRepository();
        diffs.Added.Add(Recorded(context, "@@ -1,1 +1,1 @@\n-a\n+b\n"));
        var dispatcher = new RecordingDispatcher();
        using var provider = BuildProvider(
            PatchAnsweringModel(), context, diffRepository: diffs, dispatcher: dispatcher);

        var result = await CreateWorkflow(Enabled(), provider).EvaluateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);

        Assert.Equal(RemediationCodes.SourceEvidenceMissing, result.Code);
        Assert.Empty(dispatcher.Proposals);
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
    [InlineData(RemediationProposalCodes.DiffMissing)]
    [InlineData(RemediationProposalCodes.DiffAmbiguous)]
    [InlineData(RemediationProposalCodes.DiffForeign)]
    [InlineData(RemediationProposalCodes.DiffUnsupported)]
    [InlineData(RemediationProposalCodes.BaseStale)]
    [InlineData(RemediationProposalCodes.PayloadOversized)]
    [InlineData(RemediationProposalCodes.ProposalConflict)]
    [InlineData(RemediationProposalCodes.ProposalRequested)]
    [InlineData(RemediationProposalCodes.ProposalAutoApproved)]
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
        using var provider = BuildProvider(
            model, context, workspace, ledger, tokensAlreadySpent, contextConfigured);
        return await CreateWorkflow(configuration, provider).EvaluateAsync(
            CreateIntent(), TestContext.Current.CancellationToken);
    }

    private static ServiceProvider BuildProvider(
        ScriptedModelClient model,
        RemediationPassContext? context = null,
        IRemediationWorkspace? workspace = null,
        RecordingLedgerWriter? ledger = null,
        int tokensAlreadySpent = 0,
        bool contextConfigured = true,
        IRemediationDiffRepository? diffRepository = null,
        IApplicationDispatcher? dispatcher = null)
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
        services.AddSingleton<IRemediationDiffRepository>(diffRepository ?? new RecordingDiffRepository());
        services.AddSingleton<IApplicationDispatcher>(dispatcher ?? new RecordingDispatcher());
        services.AddSingleton<RemediationDiffRunner>();
        services.AddSingleton<RemediationProposalPublisher>();
        services.AddSingleton<IRemediationPassContextRepository>(
            new StaticPassContextRepository(contextConfigured ? context ?? CreateContext() : null));
        return services.BuildServiceProvider();
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

    /// <summary>
    /// A model that answers with a bare unified diff, which is the one answer shape the pass accepts.
    /// </summary>
    private static ScriptedModelClient PatchAnsweringModel() =>
        new(request => new AiModelResponse(
            "--- a/src/Checkout.cs\n+++ b/src/Checkout.cs\n@@ -1,1 +1,1 @@\n-old\n+new\n",
            request.Model,
            "test-provider",
            new AiModelUsage(10, 10, 20),
            request.CorrelationId,
            ProposedToolCalls: []));

    /// <summary>
    /// One already-recorded diff for the given context, in the shape the pass would have written.
    /// </summary>
    private static RemediationDiff Recorded(RemediationPassContext context, string patch) => new(
        Guid.NewGuid(),
        context.Fault.TenantId,
        ReportId,
        context.Job.Id,
        context.Job.Attempt,
        context.Fault.ServiceName,
        "1.4",
        IdentifyingWorkspace.BaseIdentity,
        IdentifyingWorkspace.ResultIdentity,
        FilesChanged: 1,
        Encoding.UTF8.GetByteCount(patch),
        patch,
        "report-chat",
        "test-model",
        RemediationCodes.Applied,
        DateTimeOffset.UtcNow);

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

    /// <summary>
    /// A workspace that always names the same base and always applies. The identities are real
    /// 64-character lower-hex digests rather than readable placeholders, because the frozen proposal
    /// payload refuses anything else and a stub that could not be frozen would prove nothing about
    /// the path that freezes it.
    /// </summary>
    private sealed class IdentifyingWorkspace(string? baseTreeIdentity = null) : IRemediationWorkspace
    {
        public const string BaseIdentity = "1111111111111111111111111111111111111111111111111111111111111111";
        public const string ResultIdentity = "2222222222222222222222222222222222222222222222222222222222222222";

        public Task<RemediationBaseResult> IdentifyBaseAsync(
            RemediationTarget target,
            CancellationToken cancellationToken) =>
            Task.FromResult(RemediationBaseResult.Identified(baseTreeIdentity ?? BaseIdentity));

        public Task<RemediationApplyResult> ApplyAsync(
            RemediationApplyRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(RemediationApplyResult.Applied(ResultIdentity, 1));
    }

    /// <summary>
    /// An append-only stand-in for the diff table, scoped by tenant and report the way the real read
    /// is, so the workflow's "propose from what is already recorded" step behaves here as it does
    /// against PostgreSQL.
    /// </summary>
    private sealed class RecordingDiffRepository : IRemediationDiffRepository
    {
        public List<RemediationDiff> Added { get; } = [];

        public Task AddAsync(RemediationDiff diff, CancellationToken cancellationToken)
        {
            Added.Add(diff);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RemediationDiff>> FindForReportAsync(
            string tenantId,
            Guid reportId,
            int maximum,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemediationDiff>>(Added
                .Where(diff =>
                    string.Equals(diff.TenantId, tenantId, StringComparison.Ordinal) &&
                    diff.ReportId == reportId)
                .Take(maximum)
                .ToArray());
    }

    /// <summary>
    /// Records the proposal commands the workflow dispatches and answers them as the real handler
    /// would for an eligible origin. It is deliberately not a stub that always succeeds: the response
    /// it returns is what the workflow turns into an intent outcome.
    /// </summary>
    private sealed class RecordingDispatcher : IApplicationDispatcher
    {
        public List<ProposePostReportActionCommand> Proposals { get; } = [];

        public Task<TResponse> DispatchAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : IRequest<TResponse>
        {
            if (request is not ProposePostReportActionCommand proposal)
            {
                throw new InvalidOperationException("Only proposal commands are expected here.");
            }

            Proposals.Add(proposal);
            var response = new PostReportActionProposalResponse(
                PostReportActionProposalOutcome.Requested,
                "approval_required",
                null,
                IsReplay: Proposals.Count > 1,
                DenialAudited: false);
            return Task.FromResult((TResponse)(object)response);
        }
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
