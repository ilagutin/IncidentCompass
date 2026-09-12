using System.Text;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Retention;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Retention;
using IncidentCompass.Application.SourceContext;
using IncidentCompass.Application.Tickets;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The two retention operations against a real database. Both are destructive and irreversible, so
/// what these tests are for is the exclusion list rather than the happy path: the reap statement is
/// the only thing standing between a bounded cleanup and a report that cites an artifact which is no
/// longer there, or a sealed approval provenance chain pointing at nothing.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class RetentionLifecycleTests(PostgresRepositoryFixture postgres)
{
    private const string ReferencedArtifactPayload = """{"note":"referenced"}""";

    /// <summary>
    /// The whole exclusion list in one fixture. Two artifacts are eligible and every other row is
    /// there because something would break if it went: a report citation, an approval's proposal
    /// artifact, an approval provenance source, the two audit kinds, the job-level sentinel row, the
    /// current attempt and a stale row that is still inside the retention window. The count assertion
    /// plus the per-row assertions together say both that the right rows went and that nothing else
    /// did.
    /// </summary>
    [DockerAvailableFact]
    public async Task ReapDropsOnlyStaleAttemptArtifactsAndKeepsEveryReferencedRow()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var fixture = await SeedReapFixtureAsync(connectionString);
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);
        var before = await CountArtifactsAsync(connectionString);

        var reaped = await services.GetRequiredService<IAttemptArtifactRetentionRepository>()
            .ReapAsync(RecentCutoff(), maxRows: 100, TestContext.Current.CancellationToken);

        Assert.Equal(2, reaped);
        Assert.Equal(before - 2, await CountArtifactsAsync(connectionString));
        Assert.False(await ArtifactExistsAsync(connectionString, fixture.StaleToolResultId));
        Assert.False(await ArtifactExistsAsync(connectionString, fixture.StaleRetrievedItemId));
        Assert.True(await ArtifactExistsAsync(connectionString, fixture.CitedByReportId));
        Assert.True(await ArtifactExistsAsync(connectionString, fixture.ProposalArtifactId));
        Assert.True(await ArtifactExistsAsync(connectionString, fixture.ProvenanceSourceId));
        Assert.True(await ArtifactExistsAsync(connectionString, fixture.ActionResultId));
        Assert.True(await ArtifactExistsAsync(connectionString, fixture.JobLevelId));
        Assert.True(await ArtifactExistsAsync(connectionString, fixture.CurrentAttemptId));
        Assert.True(await ArtifactExistsAsync(connectionString, fixture.YoungStaleId));
        Assert.True(await ArtifactExistsAsync(connectionString, fixture.OriginEvidenceArtifactId));
    }

    /// <summary>
    /// A run is bounded by its row budget and a repeat run over the same window is a no-op. Together
    /// those are what let the operation be driven repeatedly, and interrupted, without any bookkeeping
    /// of its own: the predicate is the state.
    /// </summary>
    [DockerAvailableFact]
    public async Task ReapIsBoundedPerRunAndIdempotent()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        await SeedReapFixtureAsync(connectionString);
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);
        var repository = services.GetRequiredService<IAttemptArtifactRetentionRepository>();

        var first = await repository.ReapAsync(
            RecentCutoff(), maxRows: 1, TestContext.Current.CancellationToken);
        var second = await repository.ReapAsync(
            RecentCutoff(), maxRows: 100, TestContext.Current.CancellationToken);
        var third = await repository.ReapAsync(
            RecentCutoff(), maxRows: 100, TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal(0, third);
    }

    /// <summary>
    /// The age threshold is not decoration. An attempt stops being current the moment the next one is
    /// claimed, and the artifacts of the attempt that went wrong are exactly what an operator opens
    /// when they come to look at why. Reaping on the attempt predicate alone would destroy that
    /// evidence at the moment it became interesting.
    /// </summary>
    [DockerAvailableFact]
    public async Task ReapKeepsStaleAttemptArtifactsInsideTheRetentionWindow()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var fixture = await SeedReapFixtureAsync(connectionString);
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);

        var reaped = await services.GetRequiredService<IAttemptArtifactRetentionRepository>()
            .ReapAsync(
                DateTimeOffset.UtcNow.AddDays(-365),
                maxRows: 100,
                TestContext.Current.CancellationToken);

        Assert.Equal(0, reaped);
        Assert.True(await ArtifactExistsAsync(connectionString, fixture.StaleToolResultId));
        Assert.True(await ArtifactExistsAsync(connectionString, fixture.StaleRetrievedItemId));
    }

    /// <summary>
    /// Compaction empties the two raw payload columns and touches nothing else. The signal row has to
    /// survive intact: <c>faults.trigger_signal_id</c> references it, so the fault the signal opened
    /// would lose its trigger, and every derived field the pipeline reasons over was computed before
    /// the payload was stored and stays readable afterwards.
    /// </summary>
    [DockerAvailableFact]
    public async Task CompactionEmptiesOnlyTheRawPayloadColumnsAndLeavesTheFaultReferenceIntact()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        await GiveSignalAnAgedRawPayloadAsync(connectionString, origin.SignalId, ageDays: 90);
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);

        var compacted = await services.GetRequiredService<ISignalPayloadCompactionRepository>()
            .CompactAsync(RecentCutoff(), maxRows: 100, TestContext.Current.CancellationToken);
        var row = await ReadSignalAsync(connectionString, origin.SignalId);

        Assert.Equal(1, compacted);
        Assert.Equal("{}", row.Attributes);
        Assert.Equal("{}", row.Body);
        Assert.NotNull(row.PayloadCompactedAtUtc);
        Assert.Equal("compaction fixture signal", row.Summary);
        Assert.Equal("orders", row.ServiceName);
        Assert.Equal("TimeoutException", row.ErrorType);
        Assert.Equal(origin.FaultId, row.FaultId);
        Assert.Equal(
            1,
            await ActionApprovalTestSupport.CountAsync(
                connectionString,
                """
                SELECT count(*)
                FROM incidentcompass.faults AS fault
                JOIN incidentcompass.signals AS signal ON signal.id = fault.trigger_signal_id
                WHERE fault.id = @fault_id;
                """,
                ("fault_id", origin.FaultId)));
    }

    /// <summary>
    /// What an operator reading the row later can tell. A compacted payload and a payload that
    /// arrived empty are the same '{}' bytes, so the marker column is the only thing that separates
    /// them, and it is written by the backend at the moment it empties the row rather than derived
    /// from the payload afterwards.
    /// </summary>
    [DockerAvailableFact]
    public async Task CompactionMarkerSeparatesAnEmptiedPayloadFromOneThatArrivedEmpty()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        await GiveSignalAnAgedRawPayloadAsync(connectionString, origin.SignalId, ageDays: 90);
        var arrivedEmptyId = await InsertStandaloneSignalAsync(
            connectionString, attributes: "{}", body: "{}", ageDays: 0);
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);

        await services.GetRequiredService<ISignalPayloadCompactionRepository>()
            .CompactAsync(RecentCutoff(), maxRows: 100, TestContext.Current.CancellationToken);
        var compactedRow = await ReadSignalAsync(connectionString, origin.SignalId);
        var arrivedEmptyRow = await ReadSignalAsync(connectionString, arrivedEmptyId);

        Assert.Equal(compactedRow.Attributes, arrivedEmptyRow.Attributes);
        Assert.Equal(compactedRow.Body, arrivedEmptyRow.Body);
        Assert.NotNull(compactedRow.PayloadCompactedAtUtc);
        Assert.Null(arrivedEmptyRow.PayloadCompactedAtUtc);
    }

    /// <summary>
    /// A second run over the same window compacts nothing, and the marker of the first run is not
    /// rewritten. The marker column is both the exclusion and the record of when it happened, so a
    /// repeat run must not move it.
    /// </summary>
    [DockerAvailableFact]
    public async Task CompactionIsIdempotent()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        await GiveSignalAnAgedRawPayloadAsync(connectionString, origin.SignalId, ageDays: 90);
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);
        var repository = services.GetRequiredService<ISignalPayloadCompactionRepository>();

        var first = await repository.CompactAsync(
            RecentCutoff(), maxRows: 100, TestContext.Current.CancellationToken);
        var firstMarker = (await ReadSignalAsync(connectionString, origin.SignalId)).PayloadCompactedAtUtc;
        var second = await repository.CompactAsync(
            RecentCutoff(), maxRows: 100, TestContext.Current.CancellationToken);
        var secondMarker = (await ReadSignalAsync(connectionString, origin.SignalId)).PayloadCompactedAtUtc;

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(firstMarker, secondMarker);
    }

    /// <summary>
    /// The two halves of surviving retention, which are not the same property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first half is that reconstruction does not break. The ledger keeps a compact reference to
    /// a payload it does not own - <c>payload_ref</c> is plain text with no foreign key, written as
    /// <c>artifact:{id}</c> - and the payload state is resolved as a scalar expression rather than a
    /// join, so every event still comes back after its payload is gone. A reader that turned that
    /// expression into a join would silently drop entries once retention had run.
    /// </para>
    /// <para>
    /// The second half is that the timeline says so. Not crashing is not the same as being honest:
    /// before this, a reaped reference and a live one rendered as the identical string and a reader
    /// could not tell which it was holding. The <c>Retained</c> assertion before the reap is what
    /// makes the <c>Reaped</c> assertion after it mean anything - without it the test would pass
    /// against a reader that answered <c>Reaped</c> for everything.
    /// </para>
    /// </remarks>
    [DockerAvailableFact]
    public async Task ReapedLedgerPayloadIsReconstructedAndReportedAsReaped()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        var artifactId = await ActionApprovalTestSupport.SeedTicketSearchResultAsync(connectionString, origin);
        await ExecuteAsync(
            connectionString,
            """
            UPDATE incidentcompass.triage_jobs SET attempt = 2 WHERE id = @job_id;
            UPDATE incidentcompass.triage_artifacts
            SET created_at_utc = clock_timestamp() - interval '90 days'
            WHERE id = @artifact_id;
            """,
            ("job_id", origin.JobId),
            ("artifact_id", artifactId));
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);
        var ledger = services.GetRequiredService<ITriageLedgerReader>();

        var before = await ledger.ReadByFaultIdAsync(
            origin.FaultId, origin.TenantId, TestContext.Current.CancellationToken);
        var reaped = await services.GetRequiredService<IAttemptArtifactRetentionRepository>()
            .ReapAsync(RecentCutoff(), maxRows: 100, TestContext.Current.CancellationToken);
        var after = await ledger.ReadByFaultIdAsync(
            origin.FaultId, origin.TenantId, TestContext.Current.CancellationToken);

        Assert.Equal(1, reaped);
        Assert.False(await ArtifactExistsAsync(connectionString, artifactId));
        Assert.Equal(
            before.Select(static item => item.Entry.Id),
            after.Select(static item => item.Entry.Id));
        Assert.Equal(
            TriageLedgerPayloadState.Retained,
            Assert.Single(before, item => item.Entry.ToolName == "ticket_search").PayloadState);
        var toolResult = Assert.Single(after, item => item.Entry.ToolName == "ticket_search");
        Assert.Equal("artifact:" + artifactId, toolResult.Entry.PayloadRef);
        Assert.Equal(TriageLedgerPayloadState.Reaped, toolResult.PayloadState);

        // A published report is not an attempt artifact, and 018-report-lifecycle.sql rejects DELETE
        // on `triage_reports` outright, so the reap can never take one. The timeline says that
        // rather than leaving the reader to infer it from a prefix.
        var published = Assert.Single(after, item => item.Entry.ToolName == "publish_report");
        Assert.StartsWith("report:", published.Entry.PayloadRef);
        Assert.Equal(TriageLedgerPayloadState.NotReapable, published.PayloadState);
    }

    /// <summary>
    /// The compact external-action audit projection has to survive retention, and the honest way to
    /// show that is not to observe that today's cutoffs happen to spare it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both operations run here with a cutoff in the future, so nothing in this database is
    /// protected by being too young to be eligible. The reap still takes only the stale tool-result
    /// artifact: the two artifact kinds that are an audit record of a governed action are excluded
    /// by kind, and the proposal artifact is a foreign key of the approval besides.
    /// </para>
    /// <para>
    /// The last two assertions are the load-bearing ones. Equal projection columns before and after
    /// would only say that today's predicates do not reach the row. What says retention
    /// <em>cannot</em> reach it is that <c>action_approvals</c> refuses DELETE outright and refuses
    /// any UPDATE of those four columns outside the single approved-to-executed transition that sets
    /// them, so a future retention predicate that did try would abort rather than succeed quietly.
    /// </para>
    /// </remarks>
    [DockerAvailableFact]
    public async Task RetentionCannotMutateTheExternalActionAuditProjection()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        var actionId = await ActionApprovalTestSupport.CompleteGitHubIssueAsync(
            connectionString, origin, "42", "retention-projection", "retention-projection-worker");
        var staleArtifactId = await ActionApprovalTestSupport.SeedTicketSearchResultAsync(
            connectionString, origin);
        await ExecuteAsync(
            connectionString,
            "UPDATE incidentcompass.triage_jobs SET attempt = 2 WHERE id = @job_id;",
            ("job_id", origin.JobId));
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);
        var projectionBefore = await ReadProjectionAsync(connectionString, actionId);

        var reaped = await services.GetRequiredService<IAttemptArtifactRetentionRepository>()
            .ReapAsync(FutureCutoff(), maxRows: 1000, TestContext.Current.CancellationToken);
        var compacted = await services.GetRequiredService<ISignalPayloadCompactionRepository>()
            .CompactAsync(FutureCutoff(), maxRows: 1000, TestContext.Current.CancellationToken);
        var projectionAfter = await ReadProjectionAsync(connectionString, actionId);

        Assert.Equal(1, reaped);
        Assert.Equal(1, compacted);
        Assert.False(await ArtifactExistsAsync(connectionString, staleArtifactId));
        Assert.Equal("github_issue|42|absent|open", projectionBefore);
        Assert.Equal(projectionBefore, projectionAfter);
        Assert.Equal(1, await CountSurvivingActionResultAsync(connectionString, actionId));
        Assert.Equal(1, await CountResolvableProposalArtifactAsync(connectionString, actionId));

        var delete = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString,
            "DELETE FROM incidentcompass.action_approvals WHERE id = @id;",
            ("id", actionId)));
        var update = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connectionString,
            "UPDATE incidentcompass.action_approvals SET external_resource_id = '99' WHERE id = @id;",
            ("id", actionId)));

        Assert.Equal(
            "action approvals are append-only lifecycle records",
            delete.MessageText);
        Assert.Equal(
            "external action audit projection may be set only during successful terminal transition",
            update.MessageText);
        Assert.Equal(projectionBefore, await ReadProjectionAsync(connectionString, actionId));
    }

    /// <summary>
    /// The one real reader of the emptied columns. A job claimed after its signal's window has
    /// expired rehydrates through <c>PostgresTriageJobInvestigationContextRepository</c>, and both
    /// tools that read the raw payload rather than a derived column get less: <c>source_lookup</c>
    /// parses its stack trace out of <c>attributes</c> and <c>body</c>, and <c>ticket_search</c>
    /// takes its component and label terms out of <c>attributes</c>. What this asserts is that the
    /// loss is a degradation and not a failure - rehydration still succeeds, the derived fields the
    /// pipeline reasons over are all still there, and each extractor returns its empty answer rather
    /// than throwing on a payload that is now <c>{}</c>.
    /// </summary>
    [DockerAvailableFact]
    public async Task RehydratedInvestigationContextDegradesRatherThanFailsAfterItsSignalIsCompacted()
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var connectionString = database.ConnectionString;
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        await GiveSignalAnAgedRawPayloadAsync(
            connectionString,
            origin.SignalId,
            ageDays: 90,
            attributes: """
                {"exception.stacktrace":"   at Orders.Checkout.Pay() in /src/Orders/Checkout.cs:line 42",
                 "service.component":"checkout","incident.labels":["payments","p1"]}
                """,
            body: """{"raw":"the ingested payload"}""");
        using var services = ActionApprovalTestSupport.CreateServices(connectionString);
        var contexts = services.GetRequiredService<ITriageJobInvestigationContextRepository>();

        var before = await contexts.GetAsync(origin.JobId, 1, TestContext.Current.CancellationToken);
        await services.GetRequiredService<ISignalPayloadCompactionRepository>()
            .CompactAsync(RecentCutoff(), maxRows: 100, TestContext.Current.CancellationToken);
        var after = await contexts.GetAsync(origin.JobId, 1, TestContext.Current.CancellationToken);

        Assert.NotEmpty(SourceStackTraceExtractor.Extract(before.TriggerSignal));
        Assert.Equal("checkout", TicketSearchRequestFor(before).Component);
        Assert.Equal(["payments", "p1"], TicketSearchRequestFor(before).KnownLabels);

        // Rehydration still works, and everything the pipeline derived before the payload was stored
        // is still readable: the fault, the trigger-signal link and the identifying fields.
        Assert.Equal(origin.FaultId, after.Fault.Id);
        Assert.Equal(origin.SignalId, after.TriggerSignal.Id);
        Assert.Equal("orders", after.TriggerSignal.ServiceName);
        Assert.Equal("TimeoutException", after.TriggerSignal.ErrorType);
        Assert.Equal("compaction fixture signal", after.TriggerSignal.Summary);
        Assert.Equal(before.JobArtifacts.Count, after.JobArtifacts.Count);

        // What is gone is only what lived in the raw payload.
        Assert.Equal("{}", after.TriggerSignal.Attributes.GetRawText());
        Assert.Equal("{}", after.TriggerSignal.Body.GetRawText());
        Assert.Empty(SourceStackTraceExtractor.Extract(after.TriggerSignal));
        var degraded = TicketSearchRequestFor(after);
        Assert.Null(degraded.Component);
        Assert.Empty(degraded.KnownLabels);
        Assert.Equal(after.Fault.Fingerprint, degraded.Fingerprint);
        Assert.Equal("orders", degraded.ServiceName);
        Assert.Equal("TimeoutException", degraded.ErrorType);
    }

    private static TicketSearchRequest TicketSearchRequestFor(TriageJobInvestigationContext context) =>
        TicketSearchContextExtractor.Create(
            context.Fault.Fingerprint,
            context.Fault.ServiceName,
            context.TriggerSignal);

    /// <summary>
    /// Seeds one job whose current attempt is 3, with an artifact for every branch of the reap
    /// predicate. Everything except <see cref="ReapFixture.YoungStaleId"/> is aged past any cutoff
    /// these tests use, so a row that survives survives because of what references it rather than
    /// because of its age.
    /// </summary>
    private static async Task<ReapFixture> SeedReapFixtureAsync(string connectionString)
    {
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(connectionString);
        await ExecuteAsync(
            connectionString,
            "UPDATE incidentcompass.triage_jobs SET attempt = 3 WHERE id = @job_id;",
            ("job_id", origin.JobId));

        var staleToolResultId = await InsertArtifactAsync(connectionString, origin.JobId, 1, "ToolResult", ageDays: 90);
        var staleRetrievedItemId = await InsertArtifactAsync(connectionString, origin.JobId, 2, "RetrievedItem", ageDays: 90);
        var youngStaleId = await InsertArtifactAsync(connectionString, origin.JobId, 1, "ToolResult", ageDays: 0);
        var currentAttemptId = await InsertArtifactAsync(connectionString, origin.JobId, 3, "ToolResult", ageDays: 90);
        var jobLevelId = await InsertArtifactAsync(connectionString, origin.JobId, null, "NeighborSet", ageDays: 90);
        var citedId = await InsertArtifactAsync(connectionString, origin.JobId, 1, "ToolResult", ageDays: 90);
        var proposalArtifactId = await InsertArtifactAsync(connectionString, origin.JobId, 1, "ProposedAction", ageDays: 90);
        var provenanceSourceId = await InsertArtifactAsync(connectionString, origin.JobId, 1, "RetrievedItem", ageDays: 90);
        var actionResultId = await InsertArtifactAsync(connectionString, origin.JobId, 1, "ActionResult", ageDays: 90);

        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO incidentcompass.triage_evidence (
                id, report_id, kind, artifact_id, reference, created_at_utc)
            VALUES (gen_random_uuid(), @report_id, 'ToolResult', @cited_id, @reference, clock_timestamp());
            """,
            ("report_id", origin.ReportId),
            ("cited_id", citedId),
            ("reference", "artifact:" + citedId));

        var actionId = await SeedRequestedApprovalAsync(connectionString, origin, proposalArtifactId);

        // Inserted directly rather than through the proposal repository on purpose: the repository
        // will only ground provenance on an artifact that is citable for the origin attempt, so it
        // cannot produce the shape this exclusion exists for - a sealed provenance row pointing at an
        // artifact of an attempt that is no longer current. Nothing in the schema stops that row
        // being orphaned, which is exactly why the reap statement has to.
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO incidentcompass.action_approval_provenance (
                action_id, ordinal, source_type, source_id, artifact_kind, trust_class)
            VALUES (@action_id, 1, 'artifact', @source_id, 'RetrievedItem', 'untrusted_retrieved');
            """,
            ("action_id", actionId),
            ("source_id", provenanceSourceId));

        return new ReapFixture(
            origin.JobId,
            staleToolResultId,
            staleRetrievedItemId,
            youngStaleId,
            currentAttemptId,
            jobLevelId,
            citedId,
            proposalArtifactId,
            provenanceSourceId,
            actionResultId,
            origin.EvidenceArtifactId);
    }

    private static async Task<Guid> SeedRequestedApprovalAsync(
        string connectionString,
        ActionApprovalOriginFixture origin,
        Guid proposalArtifactId)
    {
        var actionId = Guid.NewGuid();
        var hash = new string('a', 64);
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO incidentcompass.action_approvals (
                id, tenant_id, origin_report_id, fault_id, job_id, attempt, tool_id, proposal_key,
                category, mode, logical_target_id, adapter_binding_fingerprint,
                approval_contract_version, provenance_sha256, state, canonical_payload,
                payload_sha256, approval_sha256, proposal_artifact_id, review_summary,
                created_at_utc, expires_at_utc)
            VALUES (
                @id, @tenant_id, @report_id, @fault_id, @job_id, 1, 'ticket_create', @proposal_key,
                'ticket_create', 'live', 'github:owner/repository', @hash,
                1, @hash, 'requested', @canonical_payload,
                @hash, @hash, @proposal_artifact_id, 'Retention exclusion fixture.',
                clock_timestamp(), clock_timestamp() + interval '1 hour');
            """,
            ("id", actionId),
            ("tenant_id", origin.TenantId),
            ("report_id", origin.ReportId),
            ("fault_id", origin.FaultId),
            ("job_id", origin.JobId),
            ("proposal_key", "retention-" + actionId.ToString("N")),
            ("hash", hash),
            ("canonical_payload", Encoding.UTF8.GetBytes("""{"title":"Retention fixture"}""")),
            ("proposal_artifact_id", proposalArtifactId));
        return actionId;
    }

    private static async Task<Guid> InsertArtifactAsync(
        string connectionString,
        Guid jobId,
        int? attempt,
        string kind,
        int ageDays)
    {
        var artifactId = Guid.NewGuid();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES (
                @id, @job_id, @attempt, @kind, @domain_ref, CAST(@payload AS jsonb), @content_hash,
                clock_timestamp() - make_interval(days => @age_days));
            """,
            ("id", artifactId),
            ("job_id", jobId),
            ("attempt", (object?)attempt ?? DBNull.Value),
            ("kind", kind),
            ("domain_ref", "retention:" + artifactId),
            ("payload", ReferencedArtifactPayload),
            ("content_hash", artifactId.ToString("N")),
            ("age_days", ageDays));
        return artifactId;
    }

    private static Task GiveSignalAnAgedRawPayloadAsync(
        string connectionString,
        Guid signalId,
        int ageDays,
        string attributes = """{"user.email":"[REDACTED]","http.route":"/checkout"}""",
        string body = """{"raw":"the ingested payload"}""") =>
        ExecuteAsync(
            connectionString,
            """
            UPDATE incidentcompass.signals
            SET attributes = CAST(@attributes AS jsonb),
                body = CAST(@body AS jsonb),
                summary = 'compaction fixture signal',
                received_at_utc = clock_timestamp() - make_interval(days => @age_days),
                observed_at_utc = clock_timestamp() - make_interval(days => @age_days)
            WHERE id = @id;
            """,
            ("id", signalId),
            ("attributes", attributes),
            ("body", body),
            ("age_days", ageDays));

    private static async Task<Guid> InsertStandaloneSignalAsync(
        string connectionString,
        string attributes,
        string body,
        int ageDays)
    {
        var signalId = Guid.NewGuid();
        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO incidentcompass.signals (
                id, tenant_id, source, fingerprint, fingerprint_version, fingerprint_strength,
                service_name, environment, error_type, summary, attributes, body,
                observed_at_utc, received_at_utc)
            VALUES (
                @id, 'tenant-action-tests', 'tester', @fingerprint, 1, 'weak',
                'orders', 'test', 'TimeoutException', 'standalone fixture signal',
                CAST(@attributes AS jsonb), CAST(@body AS jsonb),
                clock_timestamp() - make_interval(days => @age_days),
                clock_timestamp() - make_interval(days => @age_days));
            """,
            ("id", signalId),
            ("fingerprint", "standalone-" + signalId.ToString("N")),
            ("attributes", attributes),
            ("body", body),
            ("age_days", ageDays));
        return signalId;
    }

    private static async Task<SignalRow> ReadSignalAsync(string connectionString, Guid signalId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT attributes::text, body::text, payload_compacted_at_utc, summary, service_name,
                   error_type, fault_id
            FROM incidentcompass.signals
            WHERE id = @id;
            """,
            connection);
        command.Parameters.AddWithValue("id", signalId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new SignalRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6));
    }

    private static DateTimeOffset RecentCutoff() => DateTimeOffset.UtcNow.AddDays(-1);

    // A cutoff nothing in a freshly created test database can be younger than, so a row that
    // survives a run survives on an exclusion rather than on its age.
    private static DateTimeOffset FutureCutoff() => DateTimeOffset.UtcNow.AddDays(1);

    private static async Task<string> ReadProjectionAsync(string connectionString, Guid actionId) =>
        (string)(await ActionApprovalTestSupport.ScalarAsync(
            connectionString,
            """
            SELECT concat_ws('|', external_resource_kind, external_resource_id,
                             external_before_state, external_after_state)
            FROM incidentcompass.action_approvals
            WHERE id = @id;
            """,
            ("id", actionId)))!;

    private static Task<long> CountSurvivingActionResultAsync(string connectionString, Guid actionId) =>
        ActionApprovalTestSupport.CountAsync(
            connectionString,
            """
            SELECT count(*)
            FROM incidentcompass.triage_artifacts
            WHERE kind = 'ActionResult' AND domain_ref = @ref;
            """,
            ("ref", "action:" + actionId));

    private static Task<long> CountResolvableProposalArtifactAsync(string connectionString, Guid actionId) =>
        ActionApprovalTestSupport.CountAsync(
            connectionString,
            """
            SELECT count(*)
            FROM incidentcompass.action_approvals approval
            JOIN incidentcompass.triage_artifacts artifact ON artifact.id = approval.proposal_artifact_id
            WHERE approval.id = @id;
            """,
            ("id", actionId));

    private static Task<long> CountArtifactsAsync(string connectionString) =>
        ActionApprovalTestSupport.CountAsync(
            connectionString,
            "SELECT count(*) FROM incidentcompass.triage_artifacts;");

    private static async Task<bool> ArtifactExistsAsync(string connectionString, Guid artifactId) =>
        await ActionApprovalTestSupport.CountAsync(
            connectionString,
            "SELECT count(*) FROM incidentcompass.triage_artifacts WHERE id = @id;",
            ("id", artifactId)) == 1;

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed record ReapFixture(
        Guid JobId,
        Guid StaleToolResultId,
        Guid StaleRetrievedItemId,
        Guid YoungStaleId,
        Guid CurrentAttemptId,
        Guid JobLevelId,
        Guid CitedByReportId,
        Guid ProposalArtifactId,
        Guid ProvenanceSourceId,
        Guid ActionResultId,
        Guid OriginEvidenceArtifactId);

    private sealed record SignalRow(
        string Attributes,
        string Body,
        DateTimeOffset? PayloadCompactedAtUtc,
        string Summary,
        string ServiceName,
        string? ErrorType,
        Guid? FaultId);
}
