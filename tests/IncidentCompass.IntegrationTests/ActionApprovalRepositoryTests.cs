using System.Text;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents.Actions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class ActionApprovalRepositoryTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task ProposalPersistsImmutableTupleProvenanceArtifactAndExactLedger()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);

        var result = await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "proposal-basic"),
            TestContext.Current.CancellationToken);
        var detail = await services.GetRequiredService<IActionApprovalReviewRepository>().FindAsync(
            result.Action.Id, origin.TenantId, TestContext.Current.CancellationToken);

        Assert.False(result.IsReplay);
        Assert.Equal(ActionApprovalState.Requested, result.Action.State);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.Value.Provenance.Count);
        Assert.Equal("report", detail.Value.Provenance[0].SourceType);
        Assert.Equal("untrusted_prior", detail.Value.Provenance[0].TrustClass.ToStorageValue());
        Assert.Equal("untrusted_signal", detail.Value.Provenance[1].TrustClass.ToStorageValue());
        Assert.Equal(ActionApprovalContractV1.ComputePayloadSha256(result.Action.CanonicalPayload), result.Action.PayloadSha256);
        Assert.Equal(1, await CountAsync(connectionString, "action_approvals", result.Action.Id));
        Assert.Equal(1, await CountAsync(connectionString, "triage_artifacts", result.Action.ProposalArtifactId));
        Assert.Equal(2, await ActionApprovalTestSupport.CountAsync(
            connectionString,
            "SELECT count(*) FROM incidentcompass.action_approval_provenance WHERE action_id = @id;",
            ("id", result.Action.Id)));
        Assert.Equal(1, await LedgerCountAsync(connectionString, result.Action.Id, "PolicyDecision"));
        Assert.Equal(1, await LedgerCountAsync(connectionString, result.Action.Id, "ActionProposed"));
        Assert.Equal(0, await LedgerCountAsync(connectionString, result.Action.Id, "ApprovalDecision"));

        var replay = await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "proposal-basic"),
            TestContext.Current.CancellationToken);
        Assert.True(replay.IsReplay);
        Assert.Equal(result.Action.Id, replay.Action.Id);
        Assert.Equal(1, await LedgerCountAsync(connectionString, result.Action.Id, "ActionProposed"));
    }

    [DockerAvailableFact]
    public async Task ProposalAndDecisionFailureInjectionRollsBackEveryRequiredWrite()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        foreach (var point in new[]
                 {
                     ActionApprovalFaultPoint.AfterActionRow,
                     ActionApprovalFaultPoint.AfterProposalArtifact,
                     ActionApprovalFaultPoint.AfterProvenanceRow,
                     ActionApprovalFaultPoint.BeforeProposalLedger
                 })
        {
            var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
            var key = "rollback-" + point;
            using var failing = ActionApprovalTestSupport.CreateServices(
                connectionString, new ThrowingActionApprovalFaultInjector(point));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failing.GetRequiredService<IActionProposalRepository>().CreateAsync(
                    ActionApprovalTestSupport.Proposal(origin, key), TestContext.Current.CancellationToken));
            Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(
                connectionString,
                "SELECT count(*) FROM incidentcompass.action_approvals WHERE proposal_key = @key;",
                ("key", key)));
            Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(
                connectionString,
                "SELECT count(*) FROM incidentcompass.triage_artifacts WHERE job_id = @job AND kind = 'ProposedAction';",
                ("job", origin.JobId)));
        }

        var decisionOrigin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        using var normal = ActionApprovalTestSupport.CreateServices(connectionString);
        var action = (await normal.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(decisionOrigin, "decision-rollback"),
            TestContext.Current.CancellationToken)).Action;
        using var failingDecision = ActionApprovalTestSupport.CreateServices(
            connectionString, new ThrowingActionApprovalFaultInjector(ActionApprovalFaultPoint.BeforeDecisionLedger));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingDecision.GetRequiredService<IActionApprovalReviewRepository>().DecideAsync(
                Decision(action, decisionOrigin.TenantId, ActionDecisionKind.Approve),
                TestContext.Current.CancellationToken));
        var stored = await normal.GetRequiredService<IActionApprovalReviewRepository>().FindAsync(
            action.Id, decisionOrigin.TenantId, TestContext.Current.CancellationToken);
        Assert.Equal(ActionApprovalState.Requested, stored!.Value.Action.State);
        Assert.Equal(0, await LedgerCountAsync(connectionString, action.Id, "ApprovalDecision"));
    }

    [DockerAvailableFact]
    public async Task ProvenanceRejectsSelfGroundingOtherAttemptAndMissingPersistedReference()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        var workerArtifact = Guid.NewGuid();
        var otherAttemptArtifact = Guid.NewGuid();
        var invalidShapeArtifact = Guid.NewGuid();
        var proposedActionArtifact = Guid.NewGuid();
        var actionResultArtifact = Guid.NewGuid();
        var foreignOrigin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString, "tenant-foreign");
        await ActionApprovalTestSupport.ExecuteAsync(connectionString, """
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES
                (@worker, @job, 1, 'WorkerOutput', 'worker:analysis', '{}'::jsonb, @worker_hash, clock_timestamp()),
                (@other, @job, 2, 'TriggerSignal', @signal_ref, '{}'::jsonb, @other_hash, clock_timestamp()),
                (@invalid_shape, @job, 1, 'TriggerSignal', 'invalid:shape', '{}'::jsonb, @invalid_hash, clock_timestamp()),
                (@proposed, @job, 1, 'ProposedAction', @action_ref, '{}'::jsonb, @proposed_hash, clock_timestamp()),
                (@result, @job, 1, 'ActionResult', @action_ref, '{}'::jsonb, @result_hash, clock_timestamp());
            INSERT INTO incidentcompass.triage_evidence (
                id, report_id, kind, artifact_id, reference, created_at_utc)
            VALUES
                (gen_random_uuid(), @report, 'TriggerSignal', @worker, 'worker', clock_timestamp()),
                (gen_random_uuid(), @report, 'TriggerSignal', @other, 'other attempt', clock_timestamp()),
                (gen_random_uuid(), @report, 'TriggerSignal', @invalid_shape, 'invalid shape', clock_timestamp()),
                (gen_random_uuid(), @report, 'TriggerSignal', @proposed, 'proposed action', clock_timestamp()),
                (gen_random_uuid(), @report, 'TriggerSignal', @result, 'action result', clock_timestamp()),
                (gen_random_uuid(), @report, 'TriggerSignal', @foreign, 'foreign job', clock_timestamp());
            """,
            ("worker", workerArtifact), ("other", otherAttemptArtifact),
            ("invalid_shape", invalidShapeArtifact), ("proposed", proposedActionArtifact),
            ("result", actionResultArtifact), ("foreign", foreignOrigin.EvidenceArtifactId),
            ("job", origin.JobId),
            ("worker_hash", "worker-" + Guid.NewGuid()), ("other_hash", "other-" + Guid.NewGuid()),
            ("invalid_hash", "invalid-" + Guid.NewGuid()), ("proposed_hash", "proposed-" + Guid.NewGuid()),
            ("result_hash", "result-" + Guid.NewGuid()), ("action_ref", "action:" + Guid.NewGuid()),
            ("signal_ref", "signal:" + origin.SignalId), ("report", origin.ReportId));
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);
        var repository = services.GetRequiredService<IActionProposalRepository>();

        await Assert.ThrowsAsync<ActionProposalValidationException>(() => repository.CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "worker-output", [workerArtifact]),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ActionProposalValidationException>(() => repository.CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "other-attempt", [otherAttemptArtifact]),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ActionProposalValidationException>(() => repository.CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "not-persisted", [Guid.NewGuid()]),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ActionProposalValidationException>(() => repository.CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "invalid-shape", [invalidShapeArtifact]),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ActionProposalValidationException>(() => repository.CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "proposed-action", [proposedActionArtifact]),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ActionProposalValidationException>(() => repository.CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "action-result", [actionResultArtifact]),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ActionProposalValidationException>(() => repository.CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "foreign-job", [foreignOrigin.EvidenceArtifactId]),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ActionProposalValidationException>(() => repository.CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "empty", []),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(
            connectionString,
            "SELECT count(*) FROM incidentcompass.action_approvals WHERE origin_report_id = @report;",
            ("report", origin.ReportId)));
    }

    [DockerAvailableFact]
    public async Task ConcurrentDecisionHasOneWinnerAndExpiryWinsLateApproval()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        using var services = ActionApprovalTestSupport.CreateServices(connectionString, timeProvider: clock);
        var action = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "concurrent-decision"),
            TestContext.Current.CancellationToken)).Action;
        using var firstScope = services.CreateScope();
        using var secondScope = services.CreateScope();

        var results = await Task.WhenAll(
            firstScope.ServiceProvider.GetRequiredService<IActionApprovalReviewRepository>().DecideAsync(
                Decision(action, origin.TenantId, ActionDecisionKind.Approve), TestContext.Current.CancellationToken),
            secondScope.ServiceProvider.GetRequiredService<IActionApprovalReviewRepository>().DecideAsync(
                Decision(action, origin.TenantId, ActionDecisionKind.Reject), TestContext.Current.CancellationToken));
        Assert.Single(results, static result => result.Outcome == ActionDecisionOutcome.Updated);
        Assert.Single(results, static result => result.Outcome == ActionDecisionOutcome.Conflict);
        Assert.Equal(1, await LedgerCountAsync(connectionString, action.Id, "ApprovalDecision"));

        var expiring = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "expiry-wins"),
            TestContext.Current.CancellationToken)).Action;
        clock.Advance(TimeSpan.FromMinutes(61));
        var expired = await services.GetRequiredService<IActionApprovalReviewRepository>().DecideAsync(
            Decision(expiring, origin.TenantId, ActionDecisionKind.Approve),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionDecisionOutcome.Conflict, expired.Outcome);
        Assert.Equal("expired", expired.ConflictCode);
        Assert.Equal(ActionApprovalState.Expired, expired.Action!.State);
        Assert.Equal(1, await LedgerCountAsync(connectionString, expiring.Id, "ApprovalDecision"));
    }

    [DockerAvailableFact]
    public async Task ClaimAndTerminalTransitionsAreFencedAtomicAndSingleWinner()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);
        var action = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "dispatch", automaticallyApproved: true),
            TestContext.Current.CancellationToken)).Action;
        using var firstScope = services.CreateScope();
        using var secondScope = services.CreateScope();
        var claims = await Task.WhenAll(
            firstScope.ServiceProvider.GetRequiredService<IActionDispatchRepository>().TryClaimAsync(
                action.Id, "worker-a", _ => TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken),
            secondScope.ServiceProvider.GetRequiredService<IActionDispatchRepository>().TryClaimAsync(
                action.Id, "worker-b", _ => TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));
        var claim = Assert.Single(claims, static item => item is not null)!;
        Assert.Equal(1, await LedgerCountAsync(connectionString, action.Id, "ActionDispatchStarted"));

        using var failing = ActionApprovalTestSupport.CreateServices(
            connectionString, new ThrowingActionApprovalFaultInjector(ActionApprovalFaultPoint.BeforeTerminalLedger));
        var terminal = new ActionTerminalRequest(
            action.Id, claim.Fence, ActionApprovalState.Executed,
            Encoding.UTF8.GetBytes("{\"issueNumber\":\"1\",\"provider\":\"github\"}"),
            "Ticket created.",
            null,
            ExternalActionAuditProjection.GitHubIssueCreated("1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failing.GetRequiredService<IActionDispatchRepository>().CompleteAsync(
                terminal, TestContext.Current.CancellationToken));
        Assert.Equal(0, await LedgerCountAsync(connectionString, action.Id, "ActionCompleted"));
        Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(
            connectionString,
            "SELECT count(*) FROM incidentcompass.triage_artifacts WHERE job_id = @job AND kind = 'ActionResult';",
            ("job", origin.JobId)));

        Assert.True(await services.GetRequiredService<IActionDispatchRepository>().CompleteAsync(
            terminal, TestContext.Current.CancellationToken));
        Assert.False(await services.GetRequiredService<IActionDispatchRepository>().CompleteAsync(
            terminal, TestContext.Current.CancellationToken));
        Assert.Equal(1, await LedgerCountAsync(connectionString, action.Id, "ActionCompleted"));
        var stored = await services.GetRequiredService<IActionApprovalReviewRepository>().FindAsync(
            action.Id, origin.TenantId, TestContext.Current.CancellationToken);
        Assert.Equal(ActionApprovalState.Executed, stored!.Value.Action.State);
    }

    [DockerAvailableFact]
    public async Task DatabaseRejectsTupleProvenanceReviewArtifactAndTerminalMutation()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);
        var action = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "immutable"),
            TestContext.Current.CancellationToken)).Action;

        await AssertPostgresFailureAsync(connectionString,
            "UPDATE incidentcompass.action_approvals SET payload_sha256 = @hash WHERE id = @id;",
            ("hash", new string('b', 64)), ("id", action.Id));
        await AssertPostgresFailureAsync(connectionString,
            "UPDATE incidentcompass.action_approval_provenance SET trust_class = 'backend_fact' WHERE action_id = @id AND ordinal = 1;",
            ("id", action.Id));
        await AssertPostgresFailureAsync(connectionString, """
            INSERT INTO incidentcompass.action_approval_provenance (
                action_id, ordinal, source_type, source_id, artifact_kind, trust_class)
            VALUES (@id, 3, 'artifact', @source_id, 'TriggerSignal', 'untrusted_signal');
            """, ("id", action.Id), ("source_id", Guid.NewGuid()));
        await AssertPostgresFailureAsync(connectionString,
            "UPDATE incidentcompass.triage_artifacts SET redacted_payload = '{}'::jsonb WHERE id = @id;",
            ("id", action.ProposalArtifactId));

        var decision = await services.GetRequiredService<IActionApprovalReviewRepository>().DecideAsync(
            Decision(action, origin.TenantId, ActionDecisionKind.Reject),
            TestContext.Current.CancellationToken);
        Assert.Equal(ActionDecisionOutcome.Updated, decision.Outcome);
        await AssertPostgresFailureAsync(connectionString,
            "UPDATE incidentcompass.action_approvals SET rejection_reason = 'mutated' WHERE id = @id;",
            ("id", action.Id));
        await AssertPostgresFailureAsync(connectionString,
            "DELETE FROM incidentcompass.action_approvals WHERE id = @id;",
            ("id", action.Id));

        var approved = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "immutable-approved", automaticallyApproved: true),
            TestContext.Current.CancellationToken)).Action;
        await AssertPostgresFailureAsync(connectionString,
            "UPDATE incidentcompass.action_approvals SET decision_actor = 'mutated' WHERE id = @id;",
            ("id", approved.Id));
        await AssertPostgresFailureAsync(connectionString,
            "UPDATE incidentcompass.action_approvals SET result_summary = 'premature' WHERE id = @id;",
            ("id", approved.Id));
    }

    [DockerAvailableFact]
    public async Task PublicationBeforeDecisionReturnsSupersededConflictWithoutApproval()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);
        var action = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "publish-before-decision"),
            TestContext.Current.CancellationToken)).Action;
        const string workerId = "successor-publisher";
        var successor = await ActionApprovalTestSupport.SeedSuccessorJobAsync(
            database.ConnectionString, origin, workerId);

        var successorReportId = await services.GetRequiredService<ITriageReportRepository>().PublishAsync(
            successor.Job, workerId, successor.Report, TestContext.Current.CancellationToken);
        var decision = await services.GetRequiredService<IActionApprovalReviewRepository>().DecideAsync(
            Decision(action, origin.TenantId, ActionDecisionKind.Approve),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(origin.ReportId, successorReportId);
        Assert.Equal(ActionDecisionOutcome.Conflict, decision.Outcome);
        Assert.Equal("origin_report_superseded", decision.ConflictCode);
        Assert.Equal(ActionApprovalState.Requested, decision.Action!.State);
        Assert.Equal(0, await LedgerCountAsync(database.ConnectionString, action.Id, "ApprovalDecision"));
        var dispatchRepository = services.GetRequiredService<IActionDispatchRepository>();
        Assert.True(await dispatchRepository.TryFailSupersededAsync(
            action.Id, TestContext.Current.CancellationToken));
        Assert.False(await dispatchRepository.TryFailSupersededAsync(
            action.Id, TestContext.Current.CancellationToken));
        var failed = await services.GetRequiredService<IActionApprovalReviewRepository>().FindAsync(
            action.Id, origin.TenantId, TestContext.Current.CancellationToken);
        Assert.Equal(ActionApprovalState.Failed, failed!.Value.Action.State);
        Assert.Equal("origin_report_superseded", failed.Value.Action.FailureCode);
        Assert.Equal(1, await LedgerCountAsync(database.ConnectionString, action.Id, "ActionCompleted"));
        await Assert.ThrowsAsync<ActionProposalValidationException>(() =>
            services.GetRequiredService<IActionProposalRepository>().CreateAsync(
                ActionApprovalTestSupport.Proposal(origin, "stale-origin"),
                TestContext.Current.CancellationToken));
        Assert.Equal(0, await ActionApprovalTestSupport.CountAsync(
            database.ConnectionString,
            "SELECT count(*) FROM incidentcompass.action_approvals WHERE proposal_key = 'stale-origin';"));
    }

    [DockerAvailableFact]
    public async Task ConcurrentPublicationProposalDecisionAndClaimFinishWithoutDeadlockOrDuplicateEvents()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);
        var requested = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "race-decision"),
            TestContext.Current.CancellationToken)).Action;
        var approved = (await services.GetRequiredService<IActionProposalRepository>().CreateAsync(
            ActionApprovalTestSupport.Proposal(origin, "race-claim", automaticallyApproved: true),
            TestContext.Current.CancellationToken)).Action;
        const string workerId = "race-publisher";
        var successor = await ActionApprovalTestSupport.SeedSuccessorJobAsync(
            database.ConnectionString, origin, workerId);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var publishTask = StartAfterAsync(start.Task, () =>
            services.GetRequiredService<ITriageReportRepository>().PublishAsync(
                successor.Job, workerId, successor.Report, TestContext.Current.CancellationToken));
        var proposalTask = CaptureAfterAsync(start.Task, () =>
            services.GetRequiredService<IActionProposalRepository>().CreateAsync(
                ActionApprovalTestSupport.Proposal(origin, "race-new-proposal"),
                TestContext.Current.CancellationToken));
        var decisionTask = StartAfterAsync(start.Task, () =>
            services.GetRequiredService<IActionApprovalReviewRepository>().DecideAsync(
                Decision(requested, origin.TenantId, ActionDecisionKind.Approve),
                TestContext.Current.CancellationToken));
        var claimTask = StartAfterAsync(start.Task, () =>
            services.GetRequiredService<IActionDispatchRepository>().TryClaimAsync(
                approved.Id, "race-dispatcher", _ => TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken));

        start.SetResult();
        await Task.WhenAll([publishTask, proposalTask, decisionTask, claimTask])
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Null(publishTask.Exception);
        Assert.True(proposalTask.Result is null or ActionProposalValidationException);
        Assert.True(decisionTask.Result.Outcome is ActionDecisionOutcome.Updated or ActionDecisionOutcome.Conflict);
        Assert.True(claimTask.Result is null || claimTask.Result.Action.Id == approved.Id);
        Assert.Equal(1, await ActionApprovalTestSupport.CountAsync(
            database.ConnectionString,
            "SELECT count(*) FROM incidentcompass.triage_reports WHERE supersedes_report_id = @report;",
            ("report", origin.ReportId)));
        Assert.InRange(await LedgerCountAsync(database.ConnectionString, requested.Id, "ApprovalDecision"), 0, 1);
        Assert.InRange(await LedgerCountAsync(database.ConnectionString, approved.Id, "ActionDispatchStarted"), 0, 1);
        Assert.InRange(await ActionApprovalTestSupport.CountAsync(
            database.ConnectionString,
            "SELECT count(*) FROM incidentcompass.action_approvals WHERE proposal_key = 'race-new-proposal';"),
            0, 1);
    }

    [DockerAvailableFact]
    public async Task LedgerActionVocabularyDecisionStatusAndReferencesAreSchemaConstrained()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(database.ConnectionString);
        var common = """
            INSERT INTO incidentcompass.triage_ledger (
                fault_id, job_id, attempt, event_type, tool_name, decision,
                tool_status, payload_ref, config_hash, created_at_utc)
            VALUES (@fault, @job, 1, @event, 'ticket_create', @decision,
                    @status, @reference, @config, clock_timestamp());
            """;

        await AssertPostgresFailureAsync(database.ConnectionString, common,
            ("fault", origin.FaultId), ("job", origin.JobId), ("event", "ActionProposed"),
            ("decision", "Approved"), ("status", DBNull.Value),
            ("reference", "action:" + Guid.NewGuid()), ("config", origin.ConfigHash));
        await AssertPostgresFailureAsync(database.ConnectionString, common,
            ("fault", origin.FaultId), ("job", origin.JobId), ("event", "ApprovalDecision"),
            ("decision", "Allowed"), ("status", DBNull.Value),
            ("reference", "action:" + Guid.NewGuid()), ("config", origin.ConfigHash));
        await AssertPostgresFailureAsync(database.ConnectionString, common,
            ("fault", origin.FaultId), ("job", origin.JobId), ("event", "ActionCompleted"),
            ("decision", DBNull.Value), ("status", DBNull.Value),
            ("reference", "artifact:" + Guid.NewGuid()), ("config", origin.ConfigHash));
        await AssertPostgresFailureAsync(database.ConnectionString, common,
            ("fault", origin.FaultId), ("job", origin.JobId), ("event", "ActionDispatchStarted"),
            ("decision", DBNull.Value), ("status", "Succeeded"),
            ("reference", "action:" + Guid.NewGuid()), ("config", origin.ConfigHash));
        await AssertPostgresFailureAsync(database.ConnectionString, common,
            ("fault", origin.FaultId), ("job", origin.JobId), ("event", "ActionCompleted"),
            ("decision", DBNull.Value), ("status", "Failed"),
            ("reference", "report:" + origin.ReportId), ("config", origin.ConfigHash));
    }

    private static async Task<T> StartAfterAsync<T>(Task start, Func<Task<T>> action)
    {
        await start;
        return await action();
    }

    private static async Task<Exception?> CaptureAfterAsync<T>(Task start, Func<Task<T>> action)
    {
        await start;
        return await Record.ExceptionAsync(action);
    }

    private static ActionDecisionRequest Decision(
        ActionApprovalRecord action,
        string tenantId,
        ActionDecisionKind kind) =>
        new(action.Id, tenantId, "key:operator", kind, action.PayloadSha256, action.ApprovalSha256,
            kind == ActionDecisionKind.Reject ? "Not approved." : null);

    private static Task<long> CountAsync(string connectionString, string table, Guid id) =>
        ActionApprovalTestSupport.CountAsync(
            connectionString, $"SELECT count(*) FROM incidentcompass.{table} WHERE id = @id;", ("id", id));

    private static Task<long> LedgerCountAsync(string connectionString, Guid actionId, string eventType) =>
        ActionApprovalTestSupport.CountAsync(
            connectionString,
            """
            SELECT count(*)
            FROM incidentcompass.triage_ledger l
            WHERE l.event_type = @type
              AND (l.payload_ref = @ref OR l.payload_ref IN (
                  SELECT 'artifact:' || artifact.id::text
                  FROM incidentcompass.triage_artifacts artifact
                  WHERE artifact.kind = 'ActionResult' AND artifact.domain_ref = @ref));
            """,
            ("ref", "action:" + actionId), ("type", eventType));

    private static async Task AssertPostgresFailureAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var exception = await Record.ExceptionAsync(() =>
            ActionApprovalTestSupport.ExecuteAsync(connectionString, sql, parameters));
        Assert.IsType<PostgresException>(exception);
    }
}
