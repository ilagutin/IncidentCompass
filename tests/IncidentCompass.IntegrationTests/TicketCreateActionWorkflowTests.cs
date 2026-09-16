using System.Net;
using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Core.Dispatching;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.ActionApprovals.Propose;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Tickets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TicketCreateActionWorkflowTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task RepositoryBoundNoMatchStaysRequestedUntilApprovalThenDispatchesFrozenBytesOnce()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var handler = new RecordingCreateHandler();
        using var services = Services(database.ConnectionString, handler);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        await ActionApprovalTestSupport.SeedTicketSearchResultAsync(database.ConnectionString, origin);
        var intent = Intent(origin);
        var workflow = new TicketCreatePostReportActionWorkflow(
            services.GetRequiredService<ITriageConfigurationRepository>(),
            services.GetRequiredService<IServiceScopeFactory>());

        var evaluated = await workflow.EvaluateAsync(intent, TestContext.Current.CancellationToken);
        var action = await ReadActionAsync(services, database.ConnectionString, origin);

        Assert.Equal("requested", evaluated.Code);
        Assert.Equal(ActionApprovalState.Requested, action.State);
        Assert.Equal(0, handler.PostCount);
        var reviewText = Encoding.UTF8.GetString(action.CanonicalPayload) + action.ReviewSummary;
        Assert.DoesNotContain("owner/repo", reviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", reviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("api.github.com", reviewText, StringComparison.Ordinal);

        using (var scope = services.CreateScope())
        {
            var decision = await scope.ServiceProvider
                .GetRequiredService<IActionApprovalReviewRepository>()
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
                "ticket-create-test-worker",
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
            "SELECT count(*) FROM incidentcompass.triage_artifacts WHERE kind = 'ActionResult' AND domain_ref = @ref;",
            ("ref", "action:" + action.Id)));
        Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(
            database.ConnectionString,
            "SELECT count(*) FROM incidentcompass.triage_ledger WHERE event_type = 'ActionCompleted' AND tool_name = 'ticket_create' AND fault_id = @fault_id;",
            ("fault_id", origin.FaultId)));
    }

    [DockerAvailableFact]
    public async Task MissingMatchedUnavailableForeignOrDuplicateSearchOutcomeFailsClosed()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        using var services = Services(database.ConnectionString, new RecordingCreateHandler());
        var cases = new (string? Outcome, string? Provider, string? Repository, bool Duplicate)[]
        {
            (null, null, null, false),
            ("matched", "github", "owner/repo", false),
            ("connector_unavailable", null, null, false),
            ("no_match", "github", "other/repo", false),
            ("no_match", "github", "owner/repo", true)
        };

        foreach (var item in cases)
        {
            var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
            if (item.Outcome is not null)
            {
                await ActionApprovalTestSupport.SeedTicketSearchResultAsync(
                    database.ConnectionString, origin, item.Outcome, item.Provider, item.Repository);
                if (item.Duplicate)
                {
                    await ActionApprovalTestSupport.SeedTicketSearchResultAsync(
                        database.ConnectionString, origin, item.Outcome, item.Provider, item.Repository);
                }
            }

            var result = await ProposeAsync(services, origin);

            Assert.Equal(PostReportActionProposalOutcome.Denied, result.Outcome);
            Assert.Equal("ticket_create_no_match_required", result.ReasonCode);
            Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(
                database.ConnectionString,
                "SELECT count(*) FROM incidentcompass.action_approvals WHERE origin_report_id = @id;",
                ("id", origin.ReportId)));
        }
    }

    [DockerAvailableFact]
    public async Task ConcurrentEvaluationReplaysOneRequestedActionAndNeverCallsProvider()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var handler = new RecordingCreateHandler();
        using var services = Services(database.ConnectionString, handler);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        await ActionApprovalTestSupport.SeedTicketSearchResultAsync(database.ConnectionString, origin);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = StartAfterAsync(start.Task, () => ProposeAsync(services, origin));
        var second = StartAfterAsync(start.Task, () => ProposeAsync(services, origin));

        start.SetResult();
        var results = await Task.WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(results[0].Action!.Id, results[1].Action!.Id);
        Assert.Contains(results, result => result.IsReplay);
        Assert.All(results, result => Assert.Equal(ActionApprovalState.Requested, result.Action!.State));
        Assert.Equal(0, handler.PostCount);
    }

    private static ServiceProvider Services(
        string connectionString,
        RecordingCreateHandler handler)
    {
        var current = ActionDispatchTestConfiguration.Create(
            toolId: TicketCreateTool.ToolId,
            category: ActionCategory.TicketCreate,
            logicalTargetId: TicketCreateTool.LogicalTargetId);
        return ActionApprovalTestSupport.CreateServices(
            connectionString,
            configureServices: services =>
            {
                services.RemoveAll<ITriageConfigurationRepository>();
                services.AddSingleton<ITriageConfigurationRepository>(
                    new ActionDispatchTestConfigurationRepository(current));
                services.AddScoped<GitHubIssuesTicketCreate>(provider => new GitHubIssuesTicketCreate(
                    provider.GetRequiredService<IOptions<GitHubIssuesOptions>>(),
                    provider.GetRequiredService<ITicketActionHistory>(),
                    handler));
                services.AddScoped<IExternalActionTool>(provider =>
                    provider.GetRequiredService<GitHubIssuesTicketCreate>());
            });
    }

    private static async Task<ActionApprovalRecord> ReadActionAsync(
        ServiceProvider services,
        string connectionString,
        ActionApprovalOriginFixture origin)
    {
        using var scope = services.CreateScope();
        var detail = await scope.ServiceProvider.GetRequiredService<IActionApprovalReviewRepository>()
            .FindAsync(Guid.Parse((string)(await ActionApprovalTestSupport.ScalarAsync(
                connectionString,
                "SELECT id::text FROM incidentcompass.action_approvals WHERE origin_report_id = @id;",
                ("id", origin.ReportId)))!), origin.TenantId, TestContext.Current.CancellationToken);
        return Assert.IsType<(ActionApprovalRecord Action, IReadOnlyList<ActionApprovalProvenance> Provenance)>(detail).Action;
    }

    private static Task<PostReportActionProposalResponse> ProposeAsync(
        ServiceProvider services,
        ActionApprovalOriginFixture origin)
    {
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>()
            .DispatchAsync<ProposePostReportActionCommand, PostReportActionProposalResponse>(
                new ProposePostReportActionCommand(
                    origin.TenantId,
                    origin.ReportId,
                    TicketCreateTool.ToolId,
                    $"post-report:v1:{origin.ReportId:N}:{TicketCreateTool.ToolId}",
                    JsonSerializer.SerializeToElement(new
                    {
                        originReportId = origin.ReportId.ToString("N"),
                        proposalKey = $"post-report:v1:{origin.ReportId:N}:{TicketCreateTool.ToolId}"
                    })),
                TestContext.Current.CancellationToken);
    }

    private static PostReportActionIntent Intent(ActionApprovalOriginFixture origin)
    {
        var input = Encoding.UTF8.GetBytes(
            $"{{\"originReportId\":\"{origin.ReportId:N}\",\"toolId\":\"ticket_create\",\"workflowVersion\":1}}");
        return new PostReportActionIntent(
            Guid.NewGuid(), origin.TenantId, origin.ReportId, origin.FaultId, origin.JobId, 1,
            TicketCreateTool.ToolId, 1, null, origin.ConfigHash,
            $"post-report:v1:{origin.ReportId:N}:{TicketCreateTool.ToolId}", input,
            PostReportActionIntentState.Processing, "worker", Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(1),
            1, null, null, DateTimeOffset.UtcNow, null);
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
            approved.RootElement.GetProperty("title").GetString(),
            provider.RootElement.GetProperty("title").GetString());
        Assert.Equal(
            approved.RootElement.GetProperty("body").GetString(),
            provider.RootElement.GetProperty("body").GetString());
    }

    private sealed class RecordingCreateHandler : HttpMessageHandler
    {
        public int PostCount { get; private set; }
        public byte[]? ProviderRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"items\":[]}", Encoding.UTF8, "application/json")
                };
            }

            PostCount++;
            ProviderRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    "{\"number\":42,\"html_url\":\"https://github.com/owner/repo/issues/42\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
