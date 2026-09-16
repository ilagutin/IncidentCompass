using System.Globalization;
using System.Net;
using System.Text;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Governance.PostReportActions;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Notifications;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Domain.Incidents.Actions;
using IncidentCompass.Infrastructure.Notifications.Telegram;
using IncidentCompass.Infrastructure.Tickets;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class ExternalActionAuditProjectionTests(PostgresRepositoryFixture postgres)
{
    [Fact]
    public async Task ConfirmedProviderResultsCarryOnlyClosedSafeProjection()
    {
        using var telegramResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":123}}")
        };
        var telegram = await TelegramNotificationResponseParser.ParseAsync(
            telegramResponse, TestContext.Current.CancellationToken);

        var options = new GitHubIssuesOptions
        {
            Owner = "owner",
            Repository = "repo",
            Token = "secret-token"
        };
        using var createResponse = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                "{\"number\":42,\"html_url\":\"https://github.com/owner/repo/issues/42\"}")
        };
        var created = await GitHubIssueCreateResponseParser.ParseCreateAsync(
            createResponse, options, TestContext.Current.CancellationToken);
        const string marker = "0123456789abcdef";
        using var commentResponse = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                "{\"id\":77," +
                "\"html_url\":\"https://github.com/owner/repo/issues/42#issuecomment-77\"," +
                "\"issue_url\":\"https://api.github.com/repos/owner/repo/issues/42\"," +
                "\"body\":\"evidence\\n" + GitHubIssueCommentMarker.Comment(marker) + "\"}")
        };
        var updated = await GitHubIssueCommentResponseParser.ParseCreateAsync(
            commentResponse, options, 42, marker, TestContext.Current.CancellationToken);

        AssertProjection(telegram, "telegram_message", "123", "not_sent", "sent");
        AssertProjection(created, "github_issue", "42", "absent", "open");
        AssertProjection(updated, "github_issue", "42", "open", "comment_added");
        var durableText = string.Join('\n', new[]
        {
            Encoding.UTF8.GetString(telegram.CanonicalResult),
            Encoding.UTF8.GetString(created.CanonicalResult),
            Encoding.UTF8.GetString(updated.CanonicalResult)
        });
        Assert.DoesNotContain("secret-token", durableText, StringComparison.Ordinal);
        Assert.DoesNotContain("api.github.com", durableText, StringComparison.Ordinal);
        Assert.DoesNotContain("github.com/owner", durableText, StringComparison.Ordinal);
    }

    [DockerAvailableFact]
    public async Task SuccessfulProjectionCommitsAtomicallyAndIsImmutable()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);
        var repository = services.GetRequiredService<IActionProposalRepository>();
        var action = (await repository.CreateAsync(
            Proposal(
                origin,
                TelegramNotificationWorkflow.ToolIdValue,
                ActionCategory.Notification,
                "telegram:audit",
                "atomic-projection"),
            TestContext.Current.CancellationToken)).Action;
        var claim = await services.GetRequiredService<IActionDispatchRepository>().TryClaimAsync(
            action.Id, "audit-projection-worker", _ => TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claim);
        var terminal = new ActionTerminalRequest(
            action.Id,
            claim!.Fence,
            ActionApprovalState.Executed,
            Encoding.UTF8.GetBytes("{\"messageId\":\"123\",\"provider\":\"telegram\"}"),
            "Telegram accepted the notification.",
            null,
            ExternalActionAuditProjection.TelegramMessage("123"));

        using (var failing = ActionApprovalTestSupport.CreateServices(
                   database.ConnectionString,
                   new ThrowingActionApprovalFaultInjector(ActionApprovalFaultPoint.BeforeTerminalLedger)))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failing.GetRequiredService<IActionDispatchRepository>().CompleteAsync(
                    terminal, TestContext.Current.CancellationToken));
        }

        Assert.Equal("approved", await ScalarStringAsync(
            database.ConnectionString, "SELECT state FROM incidentcompass.action_approvals WHERE id = @id;", action.Id));
        Assert.Equal(0, await ProjectionCountAsync(database.ConnectionString, action.Id));
        Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(
            database.ConnectionString,
            "SELECT count(*) FROM incidentcompass.triage_artifacts WHERE kind = 'ActionResult' AND domain_ref = @ref;",
            ("ref", "action:" + action.Id)));

        Assert.True(await services.GetRequiredService<IActionDispatchRepository>().CompleteAsync(
            terminal, TestContext.Current.CancellationToken));
        var stored = await services.GetRequiredService<IActionApprovalReviewRepository>().FindAsync(
            action.Id, origin.TenantId, TestContext.Current.CancellationToken);
        AssertProjection(stored!.Value.Action.AuditProjection, "telegram_message", "123", "not_sent", "sent");
        Assert.Equal(1, await ProjectionCountAsync(database.ConnectionString, action.Id));
        Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(
            database.ConnectionString,
            "SELECT count(*) FROM incidentcompass.triage_ledger WHERE event_type = 'ActionCompleted' AND payload_ref LIKE 'artifact:%' AND job_id = @job;",
            ("job", origin.JobId)));

        var exception = await Record.ExceptionAsync(() => ActionApprovalTestSupport.ExecuteAsync(
            database.ConnectionString,
            "UPDATE incidentcompass.action_approvals SET external_resource_id = '124' WHERE id = @id;",
            ("id", action.Id)));
        Assert.IsType<PostgresException>(exception);
    }

    [DockerAvailableFact]
    public async Task TelegramCreateAndUpdateSuccessPersistClosedProjectionWhileFailureStaysNull()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);
        var cases = new[]
        {
            new ProjectionCase(
                TelegramNotificationWorkflow.ToolIdValue,
                ActionCategory.Notification,
                "telegram:audit",
                "telegram-success",
                Encoding.UTF8.GetBytes("{\"messageId\":\"123\",\"provider\":\"telegram\"}"),
                ExternalActionAuditProjection.TelegramMessage("123")),
            new ProjectionCase(
                TicketCreateTool.ToolId,
                ActionCategory.TicketCreate,
                TicketCreateTool.LogicalTargetId,
                "github-create-success",
                Encoding.UTF8.GetBytes("{\"issueNumber\":\"42\",\"provider\":\"github\"}"),
                ExternalActionAuditProjection.GitHubIssueCreated("42")),
            new ProjectionCase(
                TicketUpdatePostReportActionWorkflow.UpdateToolId,
                ActionCategory.TicketUpdate,
                TicketUpdatePostReportActionWorkflow.UpdateLogicalTargetId,
                "github-update-success",
                Encoding.UTF8.GetBytes("{\"commentId\":\"77\",\"issueNumber\":\"42\",\"provider\":\"github\"}"),
                ExternalActionAuditProjection.GitHubIssueCommentAdded("42"))
        };

        foreach (var item in cases)
        {
            var action = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
                Proposal(origin, item.ToolId, item.Category, item.LogicalTargetId, item.ProposalKey),
                TestContext.Current.CancellationToken)).Action;
            var claim = await services.GetRequiredService<IActionDispatchRepository>().TryClaimAsync(
                action.Id, "projection-case-worker", _ => TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken);
            Assert.NotNull(claim);
            Assert.True(await services.GetRequiredService<IActionDispatchRepository>().CompleteAsync(
                new ActionTerminalRequest(
                    action.Id,
                    claim!.Fence,
                    ActionApprovalState.Executed,
                    item.ResultPayload,
                    "External action succeeded.",
                    null,
                    item.Projection),
                TestContext.Current.CancellationToken));
            var stored = await services.GetRequiredService<IActionApprovalReviewRepository>().FindAsync(
                action.Id, origin.TenantId, TestContext.Current.CancellationToken);
            Assert.Equal(item.Projection, stored!.Value.Action.AuditProjection);
        }

        var failed = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            Proposal(origin, "ticket_failure", ActionCategory.TicketCreate, "github:audit", "failure-null"),
            TestContext.Current.CancellationToken)).Action;
        var failedClaim = await services.GetRequiredService<IActionDispatchRepository>().TryClaimAsync(
            failed.Id, "projection-failure-worker", _ => TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken);
        Assert.NotNull(failedClaim);
        Assert.True(await services.GetRequiredService<IActionDispatchRepository>().CompleteAsync(
            new ActionTerminalRequest(
                failed.Id,
                failedClaim!.Fence,
                ActionApprovalState.Failed,
                Encoding.UTF8.GetBytes("{\"code\":\"provider_rejected\"}"),
                "Provider rejected the action.",
                "provider_rejected"),
            TestContext.Current.CancellationToken));
        var failedStored = await services.GetRequiredService<IActionApprovalReviewRepository>().FindAsync(
            failed.Id, origin.TenantId, TestContext.Current.CancellationToken);
        Assert.Null(failedStored!.Value.Action.AuditProjection);
    }

    [DockerAvailableFact]
    public async Task CrossCategoryOrMissingSupportedProjectionCannotWriteAnyTerminalEvidence()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);
        var cases = new[]
        {
            new InvalidProjectionCase(
                TelegramNotificationWorkflow.ToolIdValue,
                ActionCategory.Notification,
                TelegramNotificationWorkflow.LogicalTargetIdValue,
                ExternalActionAuditProjection.GitHubIssueCreated("42")),
            new InvalidProjectionCase(
                TicketUpdatePostReportActionWorkflow.UpdateToolId,
                ActionCategory.TicketUpdate,
                TicketUpdatePostReportActionWorkflow.UpdateLogicalTargetId,
                ExternalActionAuditProjection.TelegramMessage("123")),
            new InvalidProjectionCase(
                TicketCreateTool.ToolId,
                ActionCategory.TicketCreate,
                TicketCreateTool.LogicalTargetId,
                ExternalActionAuditProjection.GitHubIssueCommentAdded("42")),
            new InvalidProjectionCase(
                TelegramNotificationWorkflow.ToolIdValue,
                ActionCategory.Notification,
                TelegramNotificationWorkflow.LogicalTargetIdValue,
                null)
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var action = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
                Proposal(origin, item.ToolId, item.Category, item.LogicalTargetId, $"invalid-projection-{index}"),
                TestContext.Current.CancellationToken)).Action;
            var claim = await services.GetRequiredService<IActionDispatchRepository>().TryClaimAsync(
                action.Id,
                "invalid-projection-worker",
                _ => TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken);
            Assert.NotNull(claim);

            await Assert.ThrowsAsync<ActionProposalValidationException>(() =>
                services.GetRequiredService<IActionDispatchRepository>().CompleteAsync(
                    new ActionTerminalRequest(
                        action.Id,
                        claim!.Fence,
                        ActionApprovalState.Executed,
                        Encoding.UTF8.GetBytes("{\"provider\":\"test\"}"),
                        "External action reported success.",
                        null,
                        item.Projection),
                    TestContext.Current.CancellationToken));

            Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(database.ConnectionString, """
                SELECT count(*)
                FROM incidentcompass.action_approvals
                WHERE id = @id AND state = 'approved' AND result_payload IS NULL
                  AND result_summary IS NULL AND failure_code IS NULL AND completed_at_utc IS NULL
                  AND external_resource_kind IS NULL AND external_resource_id IS NULL
                  AND external_before_state IS NULL AND external_after_state IS NULL;
                """, ("id", action.Id)));
            Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(database.ConnectionString, """
                SELECT count(*)
                FROM incidentcompass.triage_artifacts
                WHERE kind = 'ActionResult' AND domain_ref = @ref;
                """, ("ref", "action:" + action.Id)));
            Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(database.ConnectionString, """
                SELECT count(*)
                FROM incidentcompass.triage_ledger l
                JOIN incidentcompass.triage_artifacts a
                  ON l.payload_ref = 'artifact:' || a.id::text
                WHERE l.event_type = 'ActionCompleted' AND a.domain_ref = @ref;
                """, ("ref", "action:" + action.Id)));
        }
    }

    [DockerAvailableFact]
    public async Task DatabaseRejectsCrossCategoryProjectionWithoutAnyTerminalWrite()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);
        var cases = new[]
        {
            new InvalidProjectionCase(
                TelegramNotificationWorkflow.ToolIdValue,
                ActionCategory.Notification,
                TelegramNotificationWorkflow.LogicalTargetIdValue,
                ExternalActionAuditProjection.GitHubIssueCreated("42")),
            new InvalidProjectionCase(
                TicketCreateTool.ToolId,
                ActionCategory.TicketCreate,
                TicketCreateTool.LogicalTargetId,
                ExternalActionAuditProjection.GitHubIssueCommentAdded("42")),
            new InvalidProjectionCase(
                TicketUpdatePostReportActionWorkflow.UpdateToolId,
                ActionCategory.TicketUpdate,
                TicketUpdatePostReportActionWorkflow.UpdateLogicalTargetId,
                ExternalActionAuditProjection.TelegramMessage("123"))
        };

        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var action = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
                Proposal(origin, item.ToolId, item.Category, item.LogicalTargetId, $"invalid-sql-projection-{index}"),
                TestContext.Current.CancellationToken)).Action;
            var claim = await services.GetRequiredService<IActionDispatchRepository>().TryClaimAsync(
                action.Id,
                "invalid-sql-projection-worker",
                _ => TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken);
            Assert.NotNull(claim);

            var exception = await Record.ExceptionAsync(() => ActionApprovalTestSupport.ExecuteAsync(
                database.ConnectionString,
                """
                UPDATE incidentcompass.action_approvals
                SET state = 'executed',
                    result_payload = convert_to('{"provider":"test"}', 'UTF8'),
                    result_summary = 'External action reported success.',
                    completed_at_utc = now(),
                    external_resource_kind = @kind,
                    external_resource_id = @resource_id,
                    external_before_state = @before_state,
                    external_after_state = @after_state
                WHERE id = @id AND state = 'approved' AND dispatch_fence = @fence;
                """,
                ("kind", item.Projection!.ResourceKind),
                ("resource_id", item.Projection.ResourceId),
                ("before_state", item.Projection.BeforeState),
                ("after_state", item.Projection.AfterState),
                ("id", action.Id),
                ("fence", claim!.Fence)));
            var postgresException = Assert.IsType<PostgresException>(exception);
            Assert.Equal(PostgresErrorCodes.CheckViolation, postgresException.SqlState);
            Assert.Equal("ck_action_approvals_external_projection_category", postgresException.ConstraintName);

            Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(database.ConnectionString, """
                SELECT count(*)
                FROM incidentcompass.action_approvals
                WHERE id = @id AND state = 'approved' AND result_payload IS NULL
                  AND result_summary IS NULL AND failure_code IS NULL AND completed_at_utc IS NULL
                  AND external_resource_kind IS NULL AND external_resource_id IS NULL
                  AND external_before_state IS NULL AND external_after_state IS NULL;
                """, ("id", action.Id)));
            Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(database.ConnectionString, """
                SELECT count(*)
                FROM incidentcompass.triage_artifacts
                WHERE kind = 'ActionResult' AND domain_ref = @ref;
                """, ("ref", "action:" + action.Id)));
            Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(database.ConnectionString, """
                SELECT count(*)
                FROM incidentcompass.triage_ledger l
                JOIN incidentcompass.triage_artifacts a
                  ON l.payload_ref = 'artifact:' || a.id::text
                WHERE l.event_type = 'ActionCompleted' AND a.domain_ref = @ref;
                """, ("ref", "action:" + action.Id)));
        }
    }

    private static PreparedActionProposal Proposal(
        ActionApprovalOriginFixture origin,
        string toolId,
        ActionCategory category,
        string logicalTargetId,
        string proposalKey) =>
        ActionApprovalTestSupport.Proposal(origin, proposalKey, automaticallyApproved: true) with
        {
            ToolId = toolId,
            Category = category,
            LogicalTargetId = logicalTargetId
        };

    private static void AssertProjection(
        ExternalActionExecutionResult result,
        string kind,
        string id,
        string before,
        string after)
    {
        Assert.True(result.Succeeded);
        AssertProjection(result.AuditProjection, kind, id, before, after);
    }

    private static void AssertProjection(
        ExternalActionAuditProjection? projection,
        string kind,
        string id,
        string before,
        string after)
    {
        Assert.NotNull(projection);
        Assert.Equal(kind, projection.ResourceKind);
        Assert.Equal(id, projection.ResourceId);
        Assert.Equal(before, projection.BeforeState);
        Assert.Equal(after, projection.AfterState);
        projection.Validate();
    }

    private static Task<long> ProjectionCountAsync(string connectionString, Guid actionId) =>
        ActionApprovalTestSupport.CountAsync(connectionString, """
            SELECT count(*)
            FROM incidentcompass.action_approvals
            WHERE id = @id AND external_resource_kind IS NOT NULL AND external_resource_id IS NOT NULL
              AND external_before_state IS NOT NULL AND external_after_state IS NOT NULL;
            """, ("id", actionId));

    private static async Task<string> ScalarStringAsync(
        string connectionString,
        string sql,
        Guid actionId) =>
        Convert.ToString(await ActionApprovalTestSupport.ScalarAsync(
            connectionString, sql, ("id", actionId)), CultureInfo.InvariantCulture)!;

    private sealed record ProjectionCase(
        string ToolId,
        ActionCategory Category,
        string LogicalTargetId,
        string ProposalKey,
        byte[] ResultPayload,
        ExternalActionAuditProjection Projection);

    private sealed record InvalidProjectionCase(
        string ToolId,
        ActionCategory Category,
        string LogicalTargetId,
        ExternalActionAuditProjection? Projection);
}
