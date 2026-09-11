using System.Text.Json;
using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Propose;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Domain.Incidents.Actions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The ordering between an approved code write and the push that publishes it, checked against the
/// database rather than against a poller's timing.
/// </summary>
/// <remarks>
/// The claim these tests make is narrow and structural: the queue entry that can lead to a push comes
/// into existence inside the transaction that records the code write as executed, and comes into
/// existence in no other way. A report publication writes none, a dry run writes none, a failure
/// writes none, and the workflow that would read one declines to enqueue itself. So "a push cannot be
/// proposed before the change it publishes was approved and applied" is a property of a write, not a
/// convention.
/// </remarks>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class ActionSuccessorIntentTests(PostgresRepositoryFixture postgres)
{
    private const string CodeWriteToolId = "remediation_apply";

    [DockerAvailableFact]
    public async Task ExecutingAnApprovedCodeWriteEnqueuesExactlyOneBranchPushIntent()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = CodeWriteServices(database.ConnectionString);

        var action = await ExecuteApprovedAsync(database.ConnectionString, services, origin);

        var intents = await ReadIntentsAsync(
            database.ConnectionString, origin.ReportId, BranchPushToolDescriptor.ToolId);
        var intent = Assert.Single(intents);
        Assert.Equal("pending", intent.State);
        Assert.Equal(
            $"post-report:v1:{origin.ReportId:N}:{BranchPushToolDescriptor.ToolId}", intent.ProposalKey);
        Assert.Null(intent.RouteId);
        using var input = JsonDocument.Parse(intent.WorkflowInput);
        Assert.Equal(
            origin.ReportId.ToString("N"),
            input.RootElement.GetProperty("originReportId").GetString());
        Assert.Equal(
            BranchPushToolDescriptor.ToolId, input.RootElement.GetProperty("toolId").GetString());
        Assert.Equal(ActionApprovalState.Executed, action.State);
    }

    /// <summary>
    /// A simulated action never reached an adapter and applied nothing, so there is nothing to
    /// publish and no entry is written.
    /// </summary>
    [DockerAvailableFact]
    public async Task ASimulatedCodeWriteEnqueuesNothing()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = CodeWriteServices(database.ConnectionString, ActionExecutionMode.DryRun);

        var action = await ExecuteApprovedAsync(database.ConnectionString, services, origin);

        Assert.Equal(ActionApprovalState.Executed, action.State);
        Assert.Empty(await ReadIntentsAsync(
            database.ConnectionString, origin.ReportId, BranchPushToolDescriptor.ToolId));
    }

    [DockerAvailableFact]
    public async Task ACodeWriteThatFailedEnqueuesNothing()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = CodeWriteServices(
            database.ConnectionString,
            execute: static (_, _, _) => Task.FromResult(new ExternalActionExecutionResult(
                false,
                "{\"landed\":false}"u8.ToArray(),
                "The approved diff was not applied.",
                "remediation_base_mismatch")));

        var action = await ExecuteApprovedAsync(database.ConnectionString, services, origin);

        Assert.Equal(ActionApprovalState.Failed, action.State);
        Assert.Empty(await ReadIntentsAsync(
            database.ConnectionString, origin.ReportId, BranchPushToolDescriptor.ToolId));
    }

    /// <summary>
    /// The other direction: an action that schedules nothing writes nothing, so this is not a queue
    /// entry every completed action grows.
    /// </summary>
    [DockerAvailableFact]
    public async Task AnActionThatSchedulesNothingEnqueuesNothing()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = Services(
            database.ConnectionString,
            ActionDispatchTestConfiguration.Create(),
            new SyntheticExternalActionTool(),
            new AgentToolDescriptor(
                "action_test", AgentToolCapability.ExternalAction,
                ActionCategory.Notification, "test:target"));

        await ExecuteApprovedAsync(database.ConnectionString, services, origin, "action_test");

        Assert.Empty(await ReadIntentsAsync(database.ConnectionString, origin.ReportId, null));
    }

    [Theory]
    [InlineData(CodeWriteToolId, ActionCategory.CodeWrite, ActionExecutionMode.Live, ActionApprovalState.Executed, BranchPushToolDescriptor.ToolId)]
    [InlineData(CodeWriteToolId, ActionCategory.CodeWrite, ActionExecutionMode.Live, ActionApprovalState.Failed, null)]
    [InlineData(CodeWriteToolId, ActionCategory.CodeWrite, ActionExecutionMode.DryRun, ActionApprovalState.Executed, null)]
    [InlineData(CodeWriteToolId, ActionCategory.TicketCreate, ActionExecutionMode.Live, ActionApprovalState.Executed, null)]
    [InlineData("ticket_create", ActionCategory.CodeWrite, ActionExecutionMode.Live, ActionApprovalState.Executed, null)]
    [InlineData(BranchPushToolDescriptor.ToolId, ActionCategory.BranchPush, ActionExecutionMode.Live, ActionApprovalState.Executed, null)]
    public void TheSuccessorPolicyIsOneToolInOneStateAndNothingElse(
        string toolId,
        ActionCategory category,
        ActionExecutionMode mode,
        ActionApprovalState state,
        string? expected) =>
        Assert.Equal(expected, ActionSuccessorIntents.SuccessorToolId(toolId, category, mode, state));

    private static ServiceProvider CodeWriteServices(
        string connectionString,
        ActionExecutionMode mode = ActionExecutionMode.Live,
        Func<Guid, ReadOnlyMemory<byte>, CancellationToken, Task<ExternalActionExecutionResult>>? execute = null) =>
        Services(
            connectionString,
            ActionDispatchTestConfiguration.Create(
                mode: mode,
                toolId: CodeWriteToolId,
                category: ActionCategory.CodeWrite,
                logicalTargetId: RemediationApplyToolDescriptor.LogicalTargetId),

            // The real `remediation_apply` descriptor is registered by AddApplication, so this stands
            // in for the adapter only, under that descriptor's own identity. Nothing about the
            // successor rule depends on which adapter ran, which is the point of testing it this way.
            new SyntheticExternalActionTool(
                execute,
                ActionCategory.CodeWrite,
                CodeWriteToolId,
                RemediationApplyToolDescriptor.LogicalTargetId),
            descriptor: null);

    private static ServiceProvider Services(
        string connectionString,
        TriageConfiguration configuration,
        SyntheticExternalActionTool tool,
        AgentToolDescriptor? descriptor) =>
        ActionApprovalTestSupport.CreateServices(
            connectionString,
            configureServices: services =>
            {
                services.RemoveAll<ITriageConfigurationRepository>();
                services.AddSingleton<ITriageConfigurationRepository>(
                    new ActionDispatchTestConfigurationRepository(configuration));
                services.AddLogging();
                if (descriptor is not null)
                {
                    services.AddSingleton(descriptor);
                }

                services.AddSingleton<IExternalActionTool>(tool);
            });

    private static async Task<ActionApprovalRecord> ExecuteApprovedAsync(
        string connectionString,
        ServiceProvider services,
        ActionApprovalOriginFixture origin,
        string toolId = CodeWriteToolId)
    {
        using var proposeScope = services.CreateScope();
        var response = await proposeScope.ServiceProvider.GetRequiredService<IApplicationDispatcher>()
            .DispatchAsync<ProposePostReportActionCommand, PostReportActionProposalResponse>(
                new ProposePostReportActionCommand(
                    origin.TenantId,
                    origin.ReportId,
                    toolId,
                    $"post-report:v1:{origin.ReportId:N}:{toolId}",
                    JsonSerializer.SerializeToElement(new { message = "successor ordering" })),
                TestContext.Current.CancellationToken);
        var proposed = Assert.IsType<ActionApprovalRecord>(response.Action);
        if (proposed.State == ActionApprovalState.Requested)
        {
            using var decisionScope = services.CreateScope();
            await decisionScope.ServiceProvider.GetRequiredService<IActionApprovalReviewRepository>()
                .DecideAsync(
                    new ActionDecisionRequest(
                        proposed.Id,
                        origin.TenantId,
                        "successor-operator",
                        ActionDecisionKind.Approve,
                        proposed.PayloadSha256,
                        proposed.ApprovalSha256,
                        null),
                    TestContext.Current.CancellationToken);
        }

        using var dispatchScope = services.CreateScope();
        var dispatcher = dispatchScope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>();
        var claim = await dispatcher.TryClaimAsync(
            proposed.Id, "successor-worker", TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(claim);
        await dispatcher.DispatchAsync(
            claim, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        using var readScope = services.CreateScope();
        var stored = await readScope.ServiceProvider.GetRequiredService<IActionApprovalReviewRepository>()
            .FindAsync(proposed.Id, origin.TenantId, TestContext.Current.CancellationToken);
        Assert.NotNull(stored);
        return stored.Value.Action;
    }

    private static async Task<IReadOnlyList<IntentRow>> ReadIntentsAsync(
        string connectionString,
        Guid originReportId,
        string? toolId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT tool_id, state, proposal_key, route_id, workflow_input
            FROM incidentcompass.post_report_action_intents
            WHERE origin_report_id = @origin_report_id
              AND (@tool_id::text IS NULL OR tool_id = @tool_id)
            ORDER BY created_at_utc, id;
            """, connection);
        command.Parameters.AddWithValue("origin_report_id", originReportId);
        command.Parameters.AddWithValue("tool_id", (object?)toolId ?? DBNull.Value);
        var rows = new List<IntentRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new IntentRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                (byte[])reader[4]));
        }

        return rows;
    }

    private sealed record IntentRow(
        string ToolId,
        string State,
        string ProposalKey,
        string? RouteId,
        byte[] WorkflowInput);
}
