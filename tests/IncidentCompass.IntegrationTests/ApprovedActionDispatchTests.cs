using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Propose;
using IncidentCompass.Application.Governance.ActionApprovals.Testing;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class ApprovedActionDispatchTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task ConcurrentClaimsInvokeOnceWithFrozenBytesAndActionIdThenCommitAtomically()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var configuration = new ActionDispatchTestConfigurationRepository(
            ActionDispatchTestConfiguration.Create());
        var tool = new SyntheticExternalActionTool();
        using var services = Services(database.ConnectionString, configuration, tool);
        var action = await ProposeAsync(database.ConnectionString, services, "exact-bytes");

        using var firstScope = services.CreateScope();
        using var secondScope = services.CreateScope();
        var claims = await Task.WhenAll(
            firstScope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>().TryClaimAsync(
                action.Id, "action-worker-a", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            secondScope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>().TryClaimAsync(
                action.Id, "action-worker-b", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var claim = Assert.Single(claims, static item => item is not null)!;

        await firstScope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>().DispatchAsync(
            claim, TestContext.Current.CancellationToken);

        Assert.Equal(1, tool.ExecutionCalls);
        Assert.Equal(action.Id, tool.LastActionId);
        Assert.Equal(action.CanonicalPayload, tool.LastPayload);
        var stored = await ReadAsync(services, action);
        Assert.Equal(ActionApprovalState.Executed, stored.State);
        Assert.Equal(1, await LedgerCountAsync(database.ConnectionString, action.Id, "ActionDispatchStarted"));
        Assert.Equal(1, await CompletionLedgerCountAsync(database.ConnectionString, action.Id));
        Assert.Equal(1, await ResultCountAsync(database.ConnectionString, action.Id));
    }

    [DockerAvailableFact]
    public async Task ConcurrentWorkerActionPumpsInvokeOneApprovedActionAtMostOnce()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var configuration = new ActionDispatchTestConfigurationRepository(
            ActionDispatchTestConfiguration.Create());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new SyntheticExternalActionTool(async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new ExternalActionExecutionResult(
                true,
                Encoding.UTF8.GetBytes("{\"delivered\":true}"),
                "Synthetic action delivered.");
        });
        using var firstServices = Services(database.ConnectionString, configuration, tool, addPump: true);
        using var secondServices = Services(database.ConnectionString, configuration, tool, addPump: true);
        var action = await ProposeAsync(database.ConnectionString, firstServices, "multi-pump");
        var options = new ActionDispatchOptions
        {
            BatchSize = 1,
            PollIntervalSeconds = 1,
            AdapterTimeoutSeconds = 5
        };
        var firstPump = firstServices.GetRequiredService<WorkerActionPump>();
        var secondPump = secondServices.GetRequiredService<WorkerActionPump>();

        var fills = await Task.WhenAll(
            firstPump.FillAvailableSlotsAsync(
                "multi-pump-a", options, TestContext.Current.CancellationToken),
            secondPump.FillAvailableSlotsAsync(
                "multi-pump-b", options, TestContext.Current.CancellationToken));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        release.TrySetResult();
        await Task.WhenAll(ObservePumpAsync(firstPump), ObservePumpAsync(secondPump));

        Assert.InRange(fills.Sum(), 1, 2);
        Assert.Equal(1, tool.ExecutionCalls);
        Assert.Equal(ActionApprovalState.Executed, (await ReadAsync(firstServices, action)).State);
        Assert.Equal(1, await LedgerCountAsync(database.ConnectionString, action.Id, "ActionDispatchStarted"));
        Assert.Equal(1, await CompletionLedgerCountAsync(database.ConnectionString, action.Id));
    }

    [DockerAvailableFact]
    public async Task PublicationBeforeClaimPreventsCallWhilePublicationAfterClaimCannotRewriteInFlight()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var configuration = new ActionDispatchTestConfigurationRepository(
            ActionDispatchTestConfiguration.Create());
        var tool = new SyntheticExternalActionTool();
        using var services = Services(database.ConnectionString, configuration, tool);

        var staleOrigin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        var staleAction = await ProposeAsync(database.ConnectionString, services, "publish-before-claim", staleOrigin);
        var staleSuccessor = await ActionApprovalTestSupport.SeedSuccessorJobAsync(
            database.ConnectionString, staleOrigin, "publish-before-claim-worker");
        await services.GetRequiredService<ITriageReportRepository>().PublishAsync(
            staleSuccessor.Job,
            "publish-before-claim-worker",
            staleSuccessor.Report,
            TestContext.Current.CancellationToken);

        using (var scope = services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>();
            Assert.Null(await dispatcher.TryClaimAsync(
                staleAction.Id,
                "publish-before-claim-dispatcher",
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
            await dispatcher.SweepAsync(8, TestContext.Current.CancellationToken);
        }

        Assert.Equal("origin_report_superseded", (await ReadAsync(services, staleAction)).FailureCode);
        Assert.Equal(0, tool.ExecutionCalls);

        var inFlightOrigin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        var inFlightAction = await ProposeAsync(database.ConnectionString, services, "publish-after-claim", inFlightOrigin);
        using var dispatchScope = services.CreateScope();
        var inFlightDispatcher = dispatchScope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>();
        var claim = await inFlightDispatcher.TryClaimAsync(
            inFlightAction.Id,
            "publish-after-claim-dispatcher",
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claim);
        var inFlightSuccessor = await ActionApprovalTestSupport.SeedSuccessorJobAsync(
            database.ConnectionString, inFlightOrigin, "publish-after-claim-worker");
        await services.GetRequiredService<ITriageReportRepository>().PublishAsync(
            inFlightSuccessor.Job,
            "publish-after-claim-worker",
            inFlightSuccessor.Report,
            TestContext.Current.CancellationToken);

        await inFlightDispatcher.DispatchAsync(
            claim!, TestContext.Current.CancellationToken);

        Assert.Equal(ActionApprovalState.Executed, (await ReadAsync(services, inFlightAction)).State);
        Assert.Equal(1, tool.ExecutionCalls);
        Assert.Equal(1, await LedgerCountAsync(database.ConnectionString, inFlightAction.Id, "ActionDispatchStarted"));
        Assert.Equal(1, await CompletionLedgerCountAsync(database.ConnectionString, inFlightAction.Id));
    }

    [DockerAvailableFact]
    public async Task ConcurrentSweepDecisionClaimAndPublicationFinishWithoutDuplicateTransitions()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var configuration = new ActionDispatchTestConfigurationRepository(
            ActionDispatchTestConfiguration.Create(requireApproval: true));
        var tool = new SyntheticExternalActionTool();
        using var services = Services(database.ConnectionString, configuration, tool);
        var initialOrigin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        var staleSweepAction = await ProposeAsync(
            database.ConnectionString,
            services,
            "race-sweep-stale",
            initialOrigin,
            PostReportActionProposalOutcome.Requested);
        var currentSeed = await ActionApprovalTestSupport.SeedSuccessorJobAsync(
            database.ConnectionString, initialOrigin, "race-current-publisher");
        var currentReportId = await services.GetRequiredService<ITriageReportRepository>().PublishAsync(
            currentSeed.Job,
            "race-current-publisher",
            currentSeed.Report,
            TestContext.Current.CancellationToken);
        var currentOrigin = initialOrigin with
        {
            JobId = currentSeed.Job.Id,
            ReportId = currentReportId,
            EvidenceArtifactId = Guid.Parse(Assert.Single(currentSeed.Report.Evidence).ReferenceId)
        };
        var decisionAction = await ProposeAsync(
            database.ConnectionString,
            services,
            "race-decision",
            currentOrigin,
            PostReportActionProposalOutcome.Requested);
        configuration.Current = ActionDispatchTestConfiguration.Create();
        var claimAction = await ProposeAsync(
            database.ConnectionString, services, "race-claim", currentOrigin);
        var publicationSeed = await ActionApprovalTestSupport.SeedSuccessorJobAsync(
            database.ConnectionString, currentOrigin, "race-final-publisher");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sweepScope = services.CreateScope();
        using var decisionScope = services.CreateScope();
        using var claimScope = services.CreateScope();
        using var publicationScope = services.CreateScope();

        var sweepTask = StartAfterAsync(start.Task, () =>
            sweepScope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>().SweepAsync(
                8, TestContext.Current.CancellationToken));
        var decisionTask = StartAfterAsync(start.Task, () =>
            decisionScope.ServiceProvider.GetRequiredService<IActionApprovalReviewRepository>().DecideAsync(
                new ActionDecisionRequest(
                    decisionAction.Id,
                    currentOrigin.TenantId,
                    "race-operator",
                    ActionDecisionKind.Approve,
                    decisionAction.PayloadSha256,
                    decisionAction.ApprovalSha256,
                    null),
                TestContext.Current.CancellationToken));
        var claimTask = StartAfterAsync(start.Task, () =>
            claimScope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>().TryClaimAsync(
                claimAction.Id,
                "race-claim-worker",
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
        var publicationTask = StartAfterAsync(start.Task, () =>
            publicationScope.ServiceProvider.GetRequiredService<ITriageReportRepository>().PublishAsync(
                publicationSeed.Job,
                "race-final-publisher",
                publicationSeed.Report,
                TestContext.Current.CancellationToken));

        start.TrySetResult();
        await Task.WhenAll(sweepTask, decisionTask, claimTask, publicationTask)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (claimTask.Result is not null)
        {
            await claimScope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>().DispatchAsync(
                claimTask.Result,
                TestContext.Current.CancellationToken);
        }

        Assert.Equal("origin_report_superseded", (await ReadAsync(services, staleSweepAction)).FailureCode);
        Assert.Equal(
            decisionTask.Result.Outcome == ActionDecisionOutcome.Updated ? 1 : 0,
            await LedgerCountAsync(database.ConnectionString, decisionAction.Id, "ApprovalDecision"));
        Assert.Equal(
            claimTask.Result is null ? 0 : 1,
            await LedgerCountAsync(database.ConnectionString, claimAction.Id, "ActionDispatchStarted"));
        Assert.Equal(claimTask.Result is null ? 0 : 1, tool.ExecutionCalls);
        Assert.Equal(1, await CompletionLedgerCountAsync(database.ConnectionString, staleSweepAction.Id));
        Assert.InRange(await CompletionLedgerCountAsync(database.ConnectionString, claimAction.Id), 0, 1);
        Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(
            database.ConnectionString,
            "SELECT count(*) FROM incidentcompass.triage_reports WHERE supersedes_report_id = @report;",
            ("report", currentOrigin.ReportId)));
    }

    [DockerAvailableFact]
    public async Task FrozenAndTightenedDryRunSimulateWhileDisabledAndApprovalTighteningFailClosed()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var configuration = new ActionDispatchTestConfigurationRepository(
            ActionDispatchTestConfiguration.Create(ActionExecutionMode.DryRun));
        var tool = new SyntheticExternalActionTool();
        using var services = Services(database.ConnectionString, configuration, tool);

        var frozenDryRun = await ProposeAsync(database.ConnectionString, services, "frozen-dry-run");
        await ClaimAndDispatchAsync(services, frozenDryRun);
        Assert.Equal(ActionApprovalState.Executed, (await ReadAsync(services, frozenDryRun)).State);

        configuration.Current = ActionDispatchTestConfiguration.Create();
        var tightenedDryRun = await ProposeAsync(database.ConnectionString, services, "tightened-dry-run");
        configuration.Current = ActionDispatchTestConfiguration.Create(ActionExecutionMode.DryRun);
        await ClaimAndDispatchAsync(services, tightenedDryRun);
        Assert.Equal(ActionApprovalState.Executed, (await ReadAsync(services, tightenedDryRun)).State);

        configuration.Current = ActionDispatchTestConfiguration.Create();
        var disabled = await ProposeAsync(database.ConnectionString, services, "disabled-current");
        configuration.Current = ActionDispatchTestConfiguration.Create(ActionExecutionMode.Disabled);
        await ClaimAndDispatchAsync(services, disabled);
        Assert.Equal("action_disabled", (await ReadAsync(services, disabled)).FailureCode);

        configuration.Current = ActionDispatchTestConfiguration.Create();
        var approval = await ProposeAsync(database.ConnectionString, services, "approval-tightened");
        configuration.Current = ActionDispatchTestConfiguration.Create(requireApproval: true);
        await ClaimAndDispatchAsync(services, approval);
        Assert.Equal("approval_policy_changed", (await ReadAsync(services, approval)).FailureCode);
        Assert.Equal(0, tool.ExecutionCalls);
    }

    [DockerAvailableFact]
    public async Task BindingRegistryAndAdapterOutcomesFailClosedWithoutDuplicateInvocation()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var configuration = new ActionDispatchTestConfigurationRepository(
            ActionDispatchTestConfiguration.Create());
        var tool = new SyntheticExternalActionTool();
        using var services = Services(database.ConnectionString, configuration, tool);

        var bindingDrift = await ProposeAsync(database.ConnectionString, services, "binding-drift");
        tool.AdapterBindingFingerprint = new string('b', 64);
        await ClaimAndDispatchAsync(services, bindingDrift);
        Assert.Equal("adapter_binding_changed", (await ReadAsync(services, bindingDrift)).FailureCode);
        Assert.Equal(0, tool.ExecutionCalls);

        tool.AdapterBindingFingerprint = ExternalActionBinding.ComputeFingerprint(
            "synthetic", "test:target", "https://api.example.test", "resource-1");
        var unavailable = await ProposeAsync(database.ConnectionString, services, "tool-unavailable");
        using var missingToolServices = Services(
            database.ConnectionString, configuration, null, registerDescriptor: true);
        await ClaimAndDispatchAsync(missingToolServices, unavailable);
        Assert.Equal("action_tool_unavailable", (await ReadAsync(missingToolServices, unavailable)).FailureCode);

        var failedTool = new SyntheticExternalActionTool(
            static (_, _, _) => Task.FromResult(new ExternalActionExecutionResult(
                false,
                Encoding.UTF8.GetBytes("{\"accepted\":false}"),
                "Synthetic action rejected.",
                "synthetic_rejected")));
        using var failedServices = Services(database.ConnectionString, configuration, failedTool);
        var definitiveFailure = await ProposeAsync(database.ConnectionString, failedServices, "definitive-failure");
        await ClaimAndDispatchAsync(failedServices, definitiveFailure);
        Assert.Equal("synthetic_rejected", (await ReadAsync(failedServices, definitiveFailure)).FailureCode);
        Assert.Equal(1, failedTool.ExecutionCalls);
    }

    [DockerAvailableFact]
    public async Task ConfigurationTupleAndImmediateAliasDriftFailClosedWithoutAdapterCalls()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var configuration = new ActionDispatchTestConfigurationRepository(
            ActionDispatchTestConfiguration.Create());
        var tool = new SyntheticExternalActionTool();
        using var services = Services(database.ConnectionString, configuration, tool);

        var configDrift = await ProposeAsync(database.ConnectionString, services, "configuration-drift");
        configuration.Current = ActionDispatchTestConfiguration.Create(allowed: false);
        await ClaimAndDispatchAsync(services, configDrift);
        Assert.Equal("action_configuration_changed", (await ReadAsync(services, configDrift)).FailureCode);

        configuration.Current = ActionDispatchTestConfiguration.Create();
        var tupleDrift = await ProposeAsync(database.ConnectionString, services, "tuple-drift");
        var tupleTool = new SyntheticExternalActionTool(category: ActionCategory.TicketCreate);
        using var tupleServices = Services(
            database.ConnectionString,
            configuration,
            tupleTool,
            descriptorCategory: ActionCategory.TicketCreate);
        await ClaimAndDispatchAsync(tupleServices, tupleDrift);
        Assert.Equal("action_registration_changed", (await ReadAsync(tupleServices, tupleDrift)).FailureCode);

        var immediateAlias = await ProposeAsync(database.ConnectionString, services, "immediate-alias");
        using var immediateServices = Services(
            database.ConnectionString,
            configuration,
            null,
            descriptorCapability: AgentToolCapability.ImmediateRead,
            descriptorCategory: null,
            descriptorTarget: null);
        await ClaimAndDispatchAsync(immediateServices, immediateAlias);
        Assert.Equal("action_tool_unavailable", (await ReadAsync(immediateServices, immediateAlias)).FailureCode);

        Assert.Equal(0, tool.ExecutionCalls);
        Assert.Equal(0, tupleTool.ExecutionCalls);
        Assert.Equal(1, await CompletionLedgerCountAsync(database.ConnectionString, configDrift.Id));
        Assert.Equal(1, await CompletionLedgerCountAsync(database.ConnectionString, tupleDrift.Id));
        Assert.Equal(1, await CompletionLedgerCountAsync(database.ConnectionString, immediateAlias.Id));
    }

    [DockerAvailableFact]
    public async Task TerminalRollbackAndExpiredClaimRecoverUnknownWithoutReinvocation()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var configuration = new ActionDispatchTestConfigurationRepository(
            ActionDispatchTestConfiguration.Create());
        var tool = new SyntheticExternalActionTool();
        using var failingServices = Services(
            database.ConnectionString,
            configuration,
            tool,
            new ThrowingActionApprovalFaultInjector(ActionApprovalFaultPoint.BeforeTerminalLedger));
        var action = await ProposeAsync(database.ConnectionString, failingServices, "terminal-rollback");
        using var scope = failingServices.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IActionDispatchRepository>();
        var claim = await repository.TryClaimAsync(
            action.Id,
            "crashing-worker",
            _ => TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claim);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>().DispatchAsync(
                claim! with { AdapterTimeout = TimeSpan.FromSeconds(5) }, TestContext.Current.CancellationToken));
        Assert.Equal(1, tool.ExecutionCalls);
        Assert.Equal(ActionApprovalState.Approved, (await ReadAsync(failingServices, action)).State);
        Assert.Equal(0, await LedgerCountAsync(database.ConnectionString, action.Id, "ActionCompleted"));

        await Task.Delay(150, TestContext.Current.CancellationToken);
        using var recoveryServices = Services(database.ConnectionString, configuration, tool);
        using var recoveryScope = recoveryServices.CreateScope();
        await recoveryScope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>().SweepAsync(
            8, TestContext.Current.CancellationToken);

        var recovered = await ReadAsync(recoveryServices, action);
        Assert.Equal(ActionApprovalState.Failed, recovered.State);
        Assert.Equal("dispatch_outcome_unknown", recovered.FailureCode);
        Assert.Equal(1, tool.ExecutionCalls);
        Assert.Equal(1, await CompletionLedgerCountAsync(database.ConnectionString, action.Id));
        Assert.Equal(1, await ResultCountAsync(database.ConnectionString, action.Id));
        Assert.False(await repository.CompleteAsync(
            new ActionTerminalRequest(
                action.Id,
                claim!.Fence,
                ActionApprovalState.Executed,
                Encoding.UTF8.GetBytes("{\"late\":true}"),
                "Late result.",
                null),
            TestContext.Current.CancellationToken));
    }

    [DockerAvailableFact]
    public async Task PumpDrainCancelsAndObservesBlockedAdapterThenPersistsUnknownOutcome()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new SyntheticExternalActionTool(async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Synthetic action unexpectedly completed.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled.TrySetResult();
                throw;
            }
        });
        var configuration = new ActionDispatchTestConfigurationRepository(
            ActionDispatchTestConfiguration.Create());
        using var services = Services(database.ConnectionString, configuration, tool, addPump: true);
        var action = await ProposeAsync(database.ConnectionString, services, "pump-cancellation");
        var pump = services.GetRequiredService<WorkerActionPump>();
        var options = new ActionDispatchOptions
        {
            BatchSize = 1,
            PollIntervalSeconds = 1,
            AdapterTimeoutSeconds = 30
        };

        Assert.Equal(1, await pump.FillAvailableSlotsAsync(
            "pump-cancellation-worker", options, TestContext.Current.CancellationToken));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await pump.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(cancelled.Task.IsCompletedSuccessfully);
        Assert.Equal(0, pump.ActiveActionCount);
        var stored = await ReadAsync(services, action);
        Assert.Equal(ActionApprovalState.Failed, stored.State);
        Assert.Equal("dispatch_outcome_unknown", stored.FailureCode);
        Assert.Equal(1, tool.ExecutionCalls);
    }

    private static ServiceProvider Services(
        string connectionString,
        ActionDispatchTestConfigurationRepository configuration,
        SyntheticExternalActionTool? tool,
        IActionApprovalTransactionFaultInjector? faultInjector = null,
        bool registerDescriptor = true,
        bool addPump = false,
        AgentToolCapability descriptorCapability = AgentToolCapability.ExternalAction,
        ActionCategory? descriptorCategory = ActionCategory.Notification,
        string? descriptorTarget = "test:target") =>
        ActionApprovalTestSupport.CreateServices(
            connectionString,
            faultInjector,
            configureServices: services =>
            {
                services.RemoveAll<ITriageConfigurationRepository>();
                services.AddSingleton<ITriageConfigurationRepository>(configuration);
                services.AddLogging();
                if (registerDescriptor)
                {
                    services.AddSingleton(new AgentToolDescriptor(
                        "action_test",
                        descriptorCapability,
                        descriptorCategory,
                        descriptorTarget));
                }

                if (tool is not null)
                {
                    services.AddSingleton<IExternalActionTool>(tool);
                }

                if (addPump)
                {
                    services.AddSingleton<WorkerActionPump>();
                }
            });

    private static async Task<ActionApprovalRecord> ProposeAsync(
        string connectionString,
        ServiceProvider services,
        string proposalKey,
        ActionApprovalOriginFixture? origin = null,
        PostReportActionProposalOutcome expectedOutcome = PostReportActionProposalOutcome.Approved)
    {
        origin ??= await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        using var scope = services.CreateScope();
        var response = await scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>()
            .DispatchAsync<ProposePostReportActionCommand, PostReportActionProposalResponse>(
                new ProposePostReportActionCommand(
                    origin.TenantId,
                    origin.ReportId,
                    "action_test",
                    proposalKey,
                    JsonSerializer.SerializeToElement(new { message = proposalKey })),
                TestContext.Current.CancellationToken);
        Assert.True(
            response.Outcome == expectedOutcome,
            $"Proposal was {response.Outcome}: {response.ReasonCode}");
        return Assert.IsType<ActionApprovalRecord>(response.Action);
    }

    private static async Task ObservePumpAsync(WorkerActionPump pump)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (pump.ActiveActionCount > 0)
        {
            await pump.WaitForNextWakeAsync(TimeSpan.FromMilliseconds(100), timeout.Token);
            await pump.ObserveCompletedAsync(timeout.Token);
        }
    }

    private static async Task StartAfterAsync(Task start, Func<Task> action)
    {
        await start;
        await action();
    }

    private static async Task<TResult> StartAfterAsync<TResult>(Task start, Func<Task<TResult>> action)
    {
        await start;
        return await action();
    }

    private static async Task ClaimAndDispatchAsync(ServiceProvider services, ActionApprovalRecord action)
    {
        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>();
        var claim = await dispatcher.TryClaimAsync(
            action.Id,
            "approved-action-test",
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claim);
        await dispatcher.DispatchAsync(
            claim!, TestContext.Current.CancellationToken);
    }

    private static async Task<ActionApprovalRecord> ReadAsync(
        ServiceProvider services,
        ActionApprovalRecord action)
    {
        using var scope = services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IActionApprovalReviewRepository>()
            .FindAsync(action.Id, action.TenantId, TestContext.Current.CancellationToken);
        return Assert.IsType<ActionApprovalRecord>(stored?.Action);
    }

    private static Task<long> LedgerCountAsync(
        string connectionString,
        Guid actionId,
        string eventType) =>
        ActionApprovalTestSupport.CountAsync(
            connectionString,
            """
            SELECT count(*) FROM incidentcompass.triage_ledger
            WHERE event_type = @event AND payload_ref LIKE '%' || @action_id::text;
            """,
            ("event", eventType),
            ("action_id", actionId));

    private static Task<long> ResultCountAsync(string connectionString, Guid actionId) =>
        ActionApprovalTestSupport.CountAsync(
            connectionString,
            """
            SELECT count(*) FROM incidentcompass.triage_artifacts
            WHERE kind = 'ActionResult' AND domain_ref = @domain_ref;
            """,
            ("domain_ref", "action:" + actionId));

    private static Task<long> CompletionLedgerCountAsync(string connectionString, Guid actionId) =>
        ActionApprovalTestSupport.CountAsync(
            connectionString,
            """
            SELECT count(*) FROM incidentcompass.triage_ledger ledger
            JOIN incidentcompass.triage_artifacts artifact
              ON ledger.payload_ref = 'artifact:' || artifact.id::text
            WHERE ledger.event_type = 'ActionCompleted'
              AND artifact.domain_ref = @domain_ref;
            """,
            ("domain_ref", "action:" + actionId));
}
