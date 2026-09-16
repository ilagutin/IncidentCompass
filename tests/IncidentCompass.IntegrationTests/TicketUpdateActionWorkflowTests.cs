using System.Net;
using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Propose;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Tickets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TicketUpdateActionWorkflowTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task CitedTargetStaysRequestedUntilApprovalThenDispatchesFrozenCommentOnce()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var handler = new RecordingCommentHandler();
        using var services = Services(database.ConnectionString, handler);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        await TicketUpdateEvidenceResolverTests.SeedTicketAsync(
            database.ConnectionString, origin, "owner/repo", 42);
        var workflow = Workflow(services);

        var evaluated = await workflow.EvaluateAsync(
            Intent(origin), TestContext.Current.CancellationToken);
        var action = await ReadActionAsync(services, database.ConnectionString, origin);

        Assert.Equal("requested", evaluated.Code);
        Assert.Equal(ActionApprovalState.Requested, action.State);
        Assert.Equal(ActionCategory.TicketUpdate, action.Category);
        Assert.Equal(0, handler.PostCount);
        var reviewText = Encoding.UTF8.GetString(action.CanonicalPayload) + action.ReviewSummary;
        Assert.DoesNotContain("owner/repo", reviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", reviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("api.github.com", reviewText, StringComparison.Ordinal);

        using (var scope = services.CreateScope())
        {
            var decision = await scope.ServiceProvider.GetRequiredService<IActionApprovalReviewRepository>()
                .DecideAsync(new ActionDecisionRequest(
                    action.Id,
                    action.TenantId,
                    "user:ticket-operator",
                    ActionDecisionKind.Approve,
                    action.PayloadSha256,
                    action.ApprovalSha256,
                    null), TestContext.Current.CancellationToken);
            Assert.Equal(ActionDecisionOutcome.Updated, decision.Outcome);

            var dispatcher = scope.ServiceProvider.GetRequiredService<IApprovedActionDispatcher>();
            var claim = await dispatcher.TryClaimAsync(
                action.Id,
                "ticket-update-test-worker",
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.NotNull(claim);
            await dispatcher.DispatchAsync(
                claim!, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, handler.PostCount);
        AssertProviderPayloadMatchesApprovedAction(action.CanonicalPayload, handler.ProviderRequestBody);
        Assert.Equal("executed", await ActionApprovalTestSupport.ScalarAsync(
            database.ConnectionString,
            "SELECT state FROM incidentcompass.action_approvals WHERE id = @id;",
            ("id", action.Id)));
        Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(
            database.ConnectionString,
            "SELECT count(*) FROM incidentcompass.triage_ledger WHERE event_type = 'ActionCompleted' AND tool_name = 'ticket_update' AND fault_id = @fault_id;",
            ("fault_id", origin.FaultId)));
    }

    [DockerAvailableFact]
    public async Task MissingForeignMultipleOrInjectedTargetCreatesNoDispatchableAction()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var handler = new RecordingCommentHandler();
        using var services = Services(database.ConnectionString, handler);

        foreach (var scenario in new[] { "missing", "foreign", "multiple" })
        {
            var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
            if (scenario == "foreign")
            {
                await TicketUpdateEvidenceResolverTests.SeedTicketAsync(
                    database.ConnectionString, origin, "other/repo", 42);
            }
            else if (scenario == "multiple")
            {
                await TicketUpdateEvidenceResolverTests.SeedTicketAsync(
                    database.ConnectionString, origin, "owner/repo", 42);
                await TicketUpdateEvidenceResolverTests.SeedTicketAsync(
                    database.ConnectionString, origin, "owner/repo", 43);
            }

            var result = await Workflow(services).EvaluateAsync(
                Intent(origin), TestContext.Current.CancellationToken);

            Assert.Equal("ticket_update_target_required", result.Code);
            Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(
                database.ConnectionString,
                "SELECT count(*) FROM incidentcompass.action_approvals WHERE origin_report_id = @id;",
                ("id", origin.ReportId)));
        }

        var injected = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        await TicketUpdateEvidenceResolverTests.SeedTicketAsync(
            database.ConnectionString, injected, "owner/repo", 42);
        var denied = await ProposeAsync(services, injected, "99");

        Assert.Equal(PostReportActionProposalOutcome.Denied, denied.Outcome);
        Assert.Equal("ticket_update_target_required", denied.ReasonCode);
        Assert.Equal(0, handler.PostCount);
    }

    [DockerAvailableFact]
    public async Task ConcurrentEvaluationReplaysOneRequestedUpdateAndNeverCallsProvider()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var handler = new RecordingCommentHandler();
        using var services = Services(database.ConnectionString, handler);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        await TicketUpdateEvidenceResolverTests.SeedTicketAsync(
            database.ConnectionString, origin, "owner/repo", 42);
        var workflow = Workflow(services);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = StartAfterAsync(start.Task, () => workflow.EvaluateAsync(
            Intent(origin), TestContext.Current.CancellationToken));
        var second = StartAfterAsync(start.Task, () => workflow.EvaluateAsync(
            Intent(origin), TestContext.Current.CancellationToken));

        start.SetResult();
        var results = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.All(results, result => Assert.Equal("requested", result.Code));
        Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(
            database.ConnectionString,
            "SELECT count(*) FROM incidentcompass.action_approvals WHERE origin_report_id = @id;",
            ("id", origin.ReportId)));
        Assert.Equal(0, handler.PostCount);
    }

    private static ServiceProvider Services(string connectionString, RecordingCommentHandler handler)
    {
        var current = ActionDispatchTestConfiguration.Create(
            toolId: TicketUpdatePostReportActionWorkflow.UpdateToolId,
            category: ActionCategory.TicketUpdate,
            logicalTargetId: TicketUpdatePostReportActionWorkflow.UpdateLogicalTargetId);
        return ActionApprovalTestSupport.CreateServices(
            connectionString,
            configureServices: services =>
            {
                services.RemoveAll<ITriageConfigurationRepository>();
                services.AddSingleton<ITriageConfigurationRepository>(
                    new ActionDispatchTestConfigurationRepository(current));
                services.AddScoped<GitHubIssueCommentExternalActionTool>(provider =>
                    new GitHubIssueCommentExternalActionTool(
                        provider.GetRequiredService<IOptions<GitHubIssuesOptions>>(), handler));
                services.AddScoped<IExternalActionTool>(provider =>
                    provider.GetRequiredService<GitHubIssueCommentExternalActionTool>());
            });
    }

    private static TicketUpdatePostReportActionWorkflow Workflow(ServiceProvider services) => new(
        services.GetRequiredService<ITriageConfigurationRepository>(),
        services.GetRequiredService<IServiceScopeFactory>());

    private static async Task<ActionApprovalRecord> ReadActionAsync(
        ServiceProvider services,
        string connectionString,
        ActionApprovalOriginFixture origin)
    {
        using var scope = services.CreateScope();
        var id = Guid.Parse((string)(await ActionApprovalTestSupport.ScalarAsync(
            connectionString,
            "SELECT id::text FROM incidentcompass.action_approvals WHERE origin_report_id = @id;",
            ("id", origin.ReportId)))!);
        var detail = await scope.ServiceProvider.GetRequiredService<IActionApprovalReviewRepository>()
            .FindAsync(id, origin.TenantId, TestContext.Current.CancellationToken);
        return Assert.IsType<(
            ActionApprovalRecord Action,
            IReadOnlyList<ActionApprovalProvenance> Provenance)>(detail).Action;
    }

    private static Task<PostReportActionProposalResponse> ProposeAsync(
        ServiceProvider services,
        ActionApprovalOriginFixture origin,
        string ticketId)
    {
        using var scope = services.CreateScope();
        var key =
            $"post-report:v1:{origin.ReportId:N}:{TicketUpdatePostReportActionWorkflow.UpdateToolId}";
        return scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>()
            .DispatchAsync<ProposePostReportActionCommand, PostReportActionProposalResponse>(
                new ProposePostReportActionCommand(
                    origin.TenantId,
                    origin.ReportId,
                    TicketUpdatePostReportActionWorkflow.UpdateToolId,
                    key,
                    JsonSerializer.SerializeToElement(new
                    {
                        originReportId = origin.ReportId.ToString("N"),
                        proposalKey = key,
                        ticketId
                    })),
                TestContext.Current.CancellationToken);
    }

    private static PostReportActionIntent Intent(ActionApprovalOriginFixture origin)
    {
        var toolId = TicketUpdatePostReportActionWorkflow.UpdateToolId;
        var input = Encoding.UTF8.GetBytes(
            $"{{\"originReportId\":\"{origin.ReportId:N}\",\"toolId\":\"{toolId}\",\"workflowVersion\":1}}");
        return new PostReportActionIntent(
            Guid.NewGuid(), origin.TenantId, origin.ReportId, origin.FaultId, origin.JobId, 1,
            toolId, 1, null, origin.ConfigHash,
            $"post-report:v1:{origin.ReportId:N}:{toolId}", input,
            PostReportActionIntentState.Processing, "worker", Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddMinutes(1), 1, null, null, DateTimeOffset.UtcNow, null);
    }

    private static async Task<T> StartAfterAsync<T>(Task start, Func<Task<T>> action)
    {
        await start;
        return await action();
    }

    private static void AssertProviderPayloadMatchesApprovedAction(
        byte[] approvedPayload,
        byte[]? providerRequestBody)
    {
        Assert.NotNull(providerRequestBody);
        using var approved = JsonDocument.Parse(approvedPayload);
        using var provider = JsonDocument.Parse(providerRequestBody);
        Assert.Equal(
            approved.RootElement.GetProperty("body").GetString(),
            provider.RootElement.GetProperty("body").GetString());
    }

    private sealed class RecordingCommentHandler : HttpMessageHandler
    {
        public int PostCount { get; private set; }
        public byte[]? ProviderRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, Array.Empty<object>());
            }
            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, new
                {
                    number = 42,
                    html_url = "https://github.com/owner/repo/issues/42"
                });
            }

            PostCount++;
            ProviderRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            using var posted = JsonDocument.Parse(ProviderRequestBody!);
            return Json(HttpStatusCode.Created, new
            {
                id = 91,
                html_url = "https://github.com/owner/repo/issues/42#issuecomment-91",
                issue_url = "https://api.github.com/repos/owner/repo/issues/42",
                body = posted.RootElement.GetProperty("body").GetString()
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}
