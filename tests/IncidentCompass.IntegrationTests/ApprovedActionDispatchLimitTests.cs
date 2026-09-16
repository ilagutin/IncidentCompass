using System.Text.Json;
using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Propose;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// An approved action runs under its tool's own <c>TimeoutSeconds</c>, and a failed row says how far
/// dispatch got. The adapter timer runs on <see cref="ManualTimerTimeProvider"/>, so the limit fires
/// when the test advances time, while the claim deadline is the database's own interval arithmetic.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class ApprovedActionDispatchLimitTests(PostgresRepositoryFixture postgres)
{
    private const string ToolId = "action_test";
    private static readonly TimeSpan HostAdapterTimeout = TimeSpan.FromSeconds(60);

    [DockerAvailableFact]
    public async Task PerToolLimitShorterThanHostSetsBothDeadlinesAndABlockedAdapterEndsUnknown()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var configuration = new ActionDispatchTestConfigurationRepository(WithToolTimeout(7));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new SyntheticExternalActionTool(async (_, _, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Synthetic action unexpectedly completed.");
        });
        using var services = Services(database.ConnectionString, configuration, tool);
        var limited = await ProposeAsync(database.ConnectionString, services, "per-tool-limit");
        var time = new ManualTimerTimeProvider();
        using var scope = services.CreateScope();
        var dispatcher = CreateDispatcher(scope, configuration, time);

        var claim = await dispatcher.TryClaimAsync(
            limited.Id, "per-tool-limit-worker", HostAdapterTimeout, TestContext.Current.CancellationToken);

        Assert.NotNull(claim);
        Assert.Equal(TimeSpan.FromSeconds(7), claim.AdapterTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(37),
            claim.Action.DispatchDeadlineAtUtc!.Value - claim.Action.DispatchStartedAtUtc!.Value);

        var dispatch = dispatcher.DispatchAsync(claim, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(dispatch.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await dispatch.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var stored = await ReadAsync(services, limited);
        Assert.Equal(ActionApprovalState.Failed, stored.State);
        Assert.Equal(ApprovedActionDispatcher.OutcomeUnknownCode, stored.FailureCode);
        Assert.Equal(1, tool.ExecutionCalls);

        configuration.Current = ActionDispatchTestConfiguration.Create();
        var unset = await ProposeAsync(database.ConnectionString, services, "host-default-limit");
        var hostClaim = await dispatcher.TryClaimAsync(
            unset.Id, "host-default-worker", HostAdapterTimeout, TestContext.Current.CancellationToken);
        Assert.NotNull(hostClaim);
        Assert.Equal(HostAdapterTimeout, hostClaim.AdapterTimeout);
        Assert.Equal(
            TimeSpan.FromSeconds(90),
            hostClaim.Action.DispatchDeadlineAtUtc!.Value - hostClaim.Action.DispatchStartedAtUtc!.Value);
    }

    [DockerAvailableFact]
    public async Task CancellationBeforeInvocationRecordsNotInvokedWithoutCallingTheAdapter()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var configuration = new ActionDispatchTestConfigurationRepository(WithToolTimeout(7));
        var tool = new SyntheticExternalActionTool();
        using var services = Services(database.ConnectionString, configuration, tool);
        var action = await ProposeAsync(database.ConnectionString, services, "cancel-before-invoke");
        using var scope = services.CreateScope();
        var dispatcher = CreateDispatcher(scope, configuration, new ManualTimerTimeProvider());
        var claim = await dispatcher.TryClaimAsync(
            action.Id, "cancel-before-invoke-worker", HostAdapterTimeout, TestContext.Current.CancellationToken);
        Assert.NotNull(claim);
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();

        await dispatcher.DispatchAsync(claim, shutdown.Token);

        var stored = await ReadAsync(services, action);
        Assert.Equal(ActionApprovalState.Failed, stored.State);
        Assert.Equal(ApprovedActionDispatcher.NotInvokedCode, stored.FailureCode);
        Assert.Equal(0, tool.ExecutionCalls);
        Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(
            database.ConnectionString,
            """
            SELECT count(*) FROM incidentcompass.triage_ledger ledger
            JOIN incidentcompass.triage_artifacts artifact
              ON ledger.payload_ref = 'artifact:' || artifact.id::text
            WHERE ledger.event_type = 'ActionCompleted'
              AND artifact.domain_ref = @domain_ref;
            """,
            ("domain_ref", "action:" + action.Id)));

        using var retryScope = services.CreateScope();
        Assert.Null(await CreateDispatcher(retryScope, configuration, new ManualTimerTimeProvider()).TryClaimAsync(
            action.Id, "cancel-before-invoke-retry", HostAdapterTimeout, TestContext.Current.CancellationToken));
        Assert.Equal(0, tool.ExecutionCalls);
    }

    private static TriageConfiguration WithToolTimeout(int seconds)
    {
        var configuration = ActionDispatchTestConfiguration.Create();
        var tools = configuration.Tools.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        tools[ToolId] = tools[ToolId] with { TimeoutSeconds = seconds };
        return configuration with { Tools = tools };
    }

    private static ApprovedActionDispatcher CreateDispatcher(
        IServiceScope scope,
        ITriageConfigurationRepository configuration,
        TimeProvider timeProvider) =>
        new(
            scope.ServiceProvider.GetRequiredService<IActionDispatchRepository>(),
            scope.ServiceProvider.GetRequiredService<IExternalActionToolRegistry>(),
            scope.ServiceProvider.GetRequiredService<IAgentToolRegistry>(),
            configuration,
            timeProvider);

    private static ServiceProvider Services(
        string connectionString,
        ActionDispatchTestConfigurationRepository configuration,
        SyntheticExternalActionTool tool) =>
        ActionApprovalTestSupport.CreateServices(
            connectionString,
            configureServices: services =>
            {
                services.RemoveAll<ITriageConfigurationRepository>();
                services.AddSingleton<ITriageConfigurationRepository>(configuration);
                services.AddLogging();
                services.AddSingleton(new AgentToolDescriptor(
                    ToolId,
                    AgentToolCapability.ExternalAction,
                    ActionCategory.Notification,
                    "test:target"));
                services.AddSingleton<IExternalActionTool>(tool);
            });

    private static async Task<ActionApprovalRecord> ProposeAsync(
        string connectionString,
        ServiceProvider services,
        string proposalKey)
    {
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        using var scope = services.CreateScope();
        var response = await scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>()
            .DispatchAsync<ProposePostReportActionCommand, PostReportActionProposalResponse>(
                new ProposePostReportActionCommand(
                    origin.TenantId,
                    origin.ReportId,
                    ToolId,
                    proposalKey,
                    JsonSerializer.SerializeToElement(new { message = proposalKey })),
                TestContext.Current.CancellationToken);
        Assert.True(
            response.Outcome == PostReportActionProposalOutcome.Approved,
            $"Proposal was {response.Outcome}: {response.ReasonCode}");
        return Assert.IsType<ActionApprovalRecord>(response.Action);
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
}
