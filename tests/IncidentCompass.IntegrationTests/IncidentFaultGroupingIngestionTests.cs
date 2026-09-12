using System.Net;
using System.Net.Http.Json;
using IncidentCompass.Application.Investigation.Jobs;
using Microsoft.Extensions.DependencyInjection;
using static IncidentCompass.IntegrationTests.IncidentIngestionTestSupport;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class IncidentFaultGroupingIngestionTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task IngestSignal_ServiceScopedFingerprintRulePersistsAndSeparatesGenerations()
    {
        using var scope = await CreateScopeAsync(postgres, useSmallSilenceWindowConfig: true);
        var selectedFirst = await PostIngestAsync(
            scope.Client,
            TesterEnvelope("rule-checkout", "prod", "TimeoutException", "node-a timed out", "/checkout"));
        var selectedSecond = await PostIngestAsync(
            scope.Client,
            TesterEnvelope("rule-checkout", "prod", "TimeoutException", "node-b timed out", "/checkout"));
        var defaultFirst = await PostIngestAsync(
            scope.Client,
            TesterEnvelope("default-checkout", "prod", "TimeoutException", "node-a timed out", "/checkout"));
        var defaultSecond = await PostIngestAsync(
            scope.Client,
            TesterEnvelope("default-checkout", "prod", "TimeoutException", "node-b timed out", "/checkout"));

        Assert.Equal(selectedFirst.FaultId, selectedSecond.FaultId);
        Assert.NotEqual(defaultFirst.FaultId, defaultSecond.FaultId);
        Assert.Equal("route-only-checkout", await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT grouping_rule_id FROM incidentcompass.signals WHERE id = @signal_id;",
            ("signal_id", selectedFirst.SignalId)));
        Assert.Equal(2, await ScalarAsync<int>(
            scope.ConnectionString,
            "SELECT grouping_rule_version FROM incidentcompass.faults WHERE id = @fault_id;",
            ("fault_id", selectedFirst.FaultId)));
        Assert.Equal("route-only-checkout", await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT redacted_payload->>'groupingRuleId' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet';",
            ("job_id", selectedFirst.JobId!.Value)));
    }
    [DockerAvailableFact]
    public async Task IngestSignal_ServiceScopedSuppressionPolicyPersistsEffectiveFacts()
    {
        using var scope = await CreateScopeAsync(postgres, useSmallSilenceWindowConfig: true);
        var envelope = TesterEnvelope("suppression-extended", "prod", "TimeoutException", "Scoped suppression probe timed out", "/suppression-probe");

        var first = await PostIngestAsync(scope.Client, envelope);
        await ExecuteAsync(
            scope.ConnectionString,
            "UPDATE incidentcompass.faults SET created_at_utc = now() - interval '3 hours', completed_at_utc = now() - interval '2 minutes', status = 'Completed' WHERE id = @id;",
            ("id", first.FaultId));

        var second = await PostIngestAsync(scope.Client, envelope);

        Assert.True(second.IsSuppressed);
        Assert.False(second.IsNewJob);
        Assert.Equal(first.FaultId, second.FaultId);
        Assert.Equal(
            "service-extended",
            await ScalarAsync<string>(scope.ConnectionString, "SELECT suppression_rule_id FROM incidentcompass.signals WHERE id = @signal_id;", ("signal_id", second.SignalId)));
        Assert.Equal(
            60,
            await ScalarAsync<int>(scope.ConnectionString, "SELECT effective_suppression_window_minutes FROM incidentcompass.signals WHERE id = @signal_id;", ("signal_id", second.SignalId)));
        Assert.Equal(
            "service-extended",
            await ScalarAsync<string>(scope.ConnectionString, "SELECT redacted_payload->>'suppressionRuleId' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet';", ("job_id", first.JobId!.Value)));
    }

    [DockerAvailableFact]
    public async Task IngestSignal_TriggerContextRehydratesScopedSuppressionPolicy()
    {
        using var scope = await CreateScopeAsync(postgres, useSmallSilenceWindowConfig: true);
        var ingested = await PostIngestAsync(
            scope.Client,
            TesterEnvelope("suppression-extended", "prod", "TimeoutException", "Context policy probe timed out", "/suppression-context"));
        using var services = scope.Factory.Services.CreateScope();
        var repository = services.ServiceProvider.GetRequiredService<ITriageJobInvestigationContextRepository>();

        var context = await repository.GetAsync(
            ingested.JobId!.Value,
            attempt: 1,
            TestContext.Current.CancellationToken);

        Assert.Equal("service-extended", context.TriggerSignal.SuppressionRuleId);
        Assert.Equal(60, context.TriggerSignal.EffectiveSuppressionWindowMinutes);
    }

    [DockerAvailableFact]
    public async Task IngestSignal_DuplicateFingerprint_AttachesToSameFaultWithoutNewJob()
    {
        using var scope = await CreateScopeAsync(postgres);
        var envelope = TesterEnvelope("checkout-api", "prod", "NullReferenceException", "Object reference not set", "/api/checkout/confirm");

        var first = await PostIngestAsync(scope.Client, envelope);
        var second = await PostIngestAsync(scope.Client, envelope);

        Assert.Equal(first.FaultId, second.FaultId);
        Assert.True(first.IsNewJob);
        Assert.False(second.IsNewJob);

        var jobCount = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_jobs WHERE fault_id = @fault_id;",
            ("fault_id", first.FaultId));
        Assert.Equal(1, jobCount);
    }

    [DockerAvailableFact]
    public async Task IngestSignal_SilenceWindow_SuppressesThenReopensAsRecurrenceAfterWindowElapses()
    {
        using var scope = await CreateScopeAsync(postgres, useSmallSilenceWindowConfig: true);
        var envelope = TesterEnvelope("silence-window-svc", "prod", "TimeoutException", "Silence window probe timed out", "/silence-window-probe");

        var first = await PostIngestAsync(scope.Client, envelope);
        Assert.True(first.IsNewFault);

        await ExecuteAsync(
            scope.ConnectionString,
            "UPDATE incidentcompass.faults SET status = 'Completed', completed_at_utc = now() WHERE id = @id;",
            ("id", first.FaultId));

        var second = await PostIngestAsync(scope.Client, envelope);
        Assert.Equal(first.FaultId, second.FaultId);
        Assert.True(second.IsSuppressed);
        Assert.False(second.IsNewJob);

        // completed_at_utc must stay >= created_at_utc (faults_check), so back-date both together
        // far enough that completed_at_utc still clears the 1-minute silence window relative to
        // "now" when the third POST arrives.
        await ExecuteAsync(
            scope.ConnectionString,
            "UPDATE incidentcompass.faults SET created_at_utc = now() - interval '20 minutes', completed_at_utc = now() - interval '10 minutes' WHERE id = @id;",
            ("id", first.FaultId));

        var third = await PostIngestAsync(scope.Client, envelope);
        Assert.NotEqual(first.FaultId, third.FaultId);
        Assert.True(third.IsNewJob);

        var thirdFaultResponse = await scope.Client.GetAsync($"/api/v1/faults/{third.FaultId}", TestContext.Current.CancellationToken);
        var thirdFault = await thirdFaultResponse.Content.ReadFromJsonAsync<IngestionFaultDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(thirdFault);
        Assert.Equal(first.FaultId, thirdFault.RecurrenceOf);
    }

    [DockerAvailableFact]
    public async Task IngestSignal_RecurrencesIncrementStateAndCreateOneEscalationIntent()
    {
        using var scope = await CreateScopeAsync(postgres, useSmallSilenceWindowConfig: true);
        var envelope = TesterEnvelope("recurrence-state-svc", "prod", "TimeoutException", "Recurrence state probe timed out", "/recurrence-state");

        var initial = await PostIngestAsync(scope.Client, envelope);
        await CompleteOutsideSilenceWindowAsync(scope.ConnectionString, initial.FaultId);

        var firstRecurrence = await PostIngestAsync(scope.Client, envelope);
        await CompleteOutsideSilenceWindowAsync(scope.ConnectionString, firstRecurrence.FaultId);

        var secondRecurrence = await PostIngestAsync(scope.Client, envelope);
        await CompleteOutsideSilenceWindowAsync(scope.ConnectionString, secondRecurrence.FaultId);

        var laterRecurrence = await PostIngestAsync(scope.Client, envelope);

        Assert.True(firstRecurrence.IsNewJob);
        Assert.True(secondRecurrence.IsNewJob);
        Assert.True(laterRecurrence.IsNewJob);
        Assert.Equal(3, await ScalarAsync<int>(
            scope.ConnectionString,
            "SELECT recurrence_count FROM incidentcompass.recurrence_states WHERE service_name = @service_name;",
            ("service_name", "recurrence-state-svc")));
        Assert.Equal(
            secondRecurrence.JobId!.Value,
            await ScalarAsync<Guid>(
                scope.ConnectionString,
                "SELECT escalation_intent_job_id FROM incidentcompass.recurrence_states WHERE service_name = @service_name;",
                ("service_name", "recurrence-state-svc")));
        Assert.Equal(
            secondRecurrence.FaultId,
            await ScalarAsync<Guid>(
                scope.ConnectionString,
                "SELECT escalation_intent_fault_id FROM incidentcompass.recurrence_states WHERE service_name = @service_name;",
                ("service_name", "recurrence-state-svc")));
        Assert.Equal(
            "2",
            await ScalarAsync<string>(
                scope.ConnectionString,
                "SELECT redacted_payload->>'recurrenceCount' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'RecurrenceState';",
                ("job_id", secondRecurrence.JobId!.Value)));
        Assert.Equal(
            "true",
            await ScalarAsync<string>(
                scope.ConnectionString,
                "SELECT redacted_payload->>'escalationIntentCreated' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'RecurrenceState';",
                ("job_id", secondRecurrence.JobId!.Value)));
        Assert.Equal(
            "false",
            await ScalarAsync<string>(
                scope.ConnectionString,
                "SELECT redacted_payload->>'escalationIntentCreated' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'RecurrenceState';",
                ("job_id", laterRecurrence.JobId!.Value)));
    }
    [DockerAvailableFact]
    public async Task IngestSignal_ConcurrentDistinctRecurrences_CountEachAcceptedDeliveryOnce()
    {
        using var scope = await CreateScopeAsync(postgres, useSmallSilenceWindowConfig: true);
        var envelope = TesterEnvelope("concurrent-recurrence-svc", "prod", "TimeoutException", "Concurrent recurrence probe timed out", "/concurrent-recurrence");

        var initial = await PostIngestAsync(scope.Client, envelope with { Correlation = ExternalId("initial") });
        await CompleteOutsideSilenceWindowAsync(scope.ConnectionString, initial.FaultId);

        var recurrences = await Task.WhenAll(
            PostIngestAsync(scope.Client, envelope with
            {
                ObservedAtUtc = new DateTimeOffset(2026, 7, 14, 1, 2, 0, TimeSpan.Zero),
                Correlation = ExternalId("recurrence-one")
            }),
            PostIngestAsync(scope.Client, envelope with
            {
                ObservedAtUtc = new DateTimeOffset(2026, 7, 14, 1, 1, 0, TimeSpan.Zero),
                Correlation = ExternalId("recurrence-two")
            }));

        var recurrenceFaultId = Assert.Single(recurrences.Select(recurrence => recurrence.FaultId).Distinct());
        var recurrenceJobId = Assert.Single(recurrences, recurrence => recurrence.JobId is not null).JobId!.Value;
        Assert.NotEqual(initial.FaultId, recurrenceFaultId);
        Assert.Equal(2, await ScalarAsync<int>(
            scope.ConnectionString,
            "SELECT recurrence_count FROM incidentcompass.recurrence_states WHERE service_name = @service_name;",
            ("service_name", "concurrent-recurrence-svc")));
        Assert.True(await ScalarAsync<bool>(
            scope.ConnectionString,
            "SELECT last_recurrence_at_utc > first_recurrence_at_utc FROM incidentcompass.recurrence_states WHERE service_name = @service_name;",
            ("service_name", "concurrent-recurrence-svc")));
        Assert.Equal(recurrenceJobId, await ScalarAsync<Guid>(
            scope.ConnectionString,
            "SELECT escalation_intent_job_id FROM incidentcompass.recurrence_states WHERE service_name = @service_name;",
            ("service_name", "concurrent-recurrence-svc")));
        Assert.Equal("2", await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT redacted_payload->>'recurrenceCount' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'RecurrenceState';",
            ("job_id", recurrenceJobId)));
        Assert.Equal("true", await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT redacted_payload->>'escalationIntentCreated' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'RecurrenceState';",
            ("job_id", recurrenceJobId)));
        Assert.Equal("2026-07-14T01:02:00.0000000+00:00", await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT redacted_payload->>'lastRecurrenceAtUtc' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'RecurrenceState';",
            ("job_id", recurrenceJobId)));

        var duplicate = await PostIngestAsync(scope.Client, envelope with { Correlation = ExternalId("recurrence-one") });

        Assert.False(duplicate.IsNewJob);
        Assert.Equal(2, await ScalarAsync<int>(
            scope.ConnectionString,
            "SELECT recurrence_count FROM incidentcompass.recurrence_states WHERE service_name = @service_name;",
            ("service_name", "concurrent-recurrence-svc")));
    }

    [DockerAvailableFact]
    public async Task IngestSignal_NeighborCount_DeduplicatesRepeatedExternalId()
    {
        using var scope = await CreateScopeAsync(postgres);
        var envelope = TesterEnvelope("sql-dedupe-neighbor-svc", "prod", "TimeoutException", "SQL dedupe probe timed out", "/sql-dedupe-probe");

        var first = await PostIngestAsync(scope.Client, envelope with { Correlation = ExternalId("duplicate-external-id") });
        var duplicate = await PostIngestAsync(scope.Client, envelope with { Correlation = ExternalId("duplicate-external-id") });
        Assert.Equal(first.FaultId, duplicate.FaultId);
        Assert.Equal(first.SignalId, duplicate.SignalId);
        Assert.False(duplicate.IsNewFault);
        Assert.False(duplicate.IsNewJob);

        var acceptedDeliveryCount = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.signals WHERE delivery_key = @delivery_key;",
            ("delivery_key", "external:duplicate-external-id"));
        Assert.Equal(1, acceptedDeliveryCount);

        await ExecuteAsync(
            scope.ConnectionString,
            "UPDATE incidentcompass.faults SET created_at_utc = now() - interval '2 days', completed_at_utc = now() - interval '1 day', status = 'Completed' WHERE id = @id;",
            ("id", first.FaultId));

        var recurrence = await PostIngestAsync(scope.Client, envelope with { Correlation = ExternalId("unique-external-id") });
        Assert.True(recurrence.IsNewJob);

        var neighborCount = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT redacted_payload->>'neighborCount' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet';",
            ("job_id", recurrence.JobId!.Value));
        Assert.Equal("2", neighborCount);
    }

    [DockerAvailableFact]
    public async Task IngestSignal_ConcurrentAttachments_KeepSingleNeighborSetArtifact()
    {
        using var scope = await CreateScopeAsync(postgres);
        var envelope = TesterEnvelope(
            "concurrent-neighbor-svc-" + Guid.NewGuid().ToString("N"),
            "prod",
            "TimeoutException",
            "Concurrent neighbor probe timed out",
            "/concurrent-neighbor");

        var first = await PostIngestAsync(scope.Client, envelope);
        var attached = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            PostIngestAsync(
                scope.Client,
                envelope with { Correlation = ExternalId("concurrent-neighbor-" + index) })));

        Assert.All(attached, item =>
        {
            Assert.Equal(first.FaultId, item.FaultId);
            Assert.False(item.IsNewJob);
        });

        var neighborRows = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet' AND attempt IS NULL;",
            ("job_id", first.JobId!.Value));
        Assert.Equal(1, neighborRows);
    }

    [DockerAvailableFact]
    public async Task IngestSignal_RefreshNeighborSet_DoesNotRequirePartialUniqueIndex()
    {
        using var scope = await CreateScopeAsync(postgres);
        await ExecuteAsync(
            scope.ConnectionString,
            "DROP INDEX IF EXISTS incidentcompass.ux_triage_artifacts_job_level_kind;");
        try
        {
            var envelope = TesterEnvelope(
                "legacy-neighbor-index-svc-" + Guid.NewGuid().ToString("N"),
                "prod",
                "TimeoutException",
                "Legacy neighbor index probe timed out",
                "/legacy-neighbor-index");

            var first = await PostIngestAsync(scope.Client, envelope);
            var attached = await PostIngestAsync(scope.Client, envelope with { Correlation = ExternalId("legacy-neighbor-index-2") });

            Assert.Equal(first.FaultId, attached.FaultId);
            Assert.False(attached.IsNewJob);

            var neighborRows = await ScalarAsync<long>(
                scope.ConnectionString,
                "SELECT COUNT(*) FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet' AND attempt IS NULL;",
                ("job_id", first.JobId!.Value));
            Assert.Equal(1, neighborRows);

            var neighborCount = await ScalarAsync<string>(
                scope.ConnectionString,
                "SELECT redacted_payload->>'neighborCount' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet';",
                ("job_id", first.JobId.Value));
            Assert.Equal("2", neighborCount);
        }
        finally
        {
            await PostgresSchemaTestHelper.EnsureSchemaAsync(scope.ConnectionString);
        }
    }

    [DockerAvailableFact]
    public async Task IngestSignal_ConcurrentSameFingerprintStrongSignals_SettleOnOneFaultWithoutUniqueViolation()
    {
        using var scope = await CreateScopeAsync(postgres);
        var envelope = TesterEnvelope("concurrent-strong-svc", "prod", "TimeoutException", "Concurrent probe timed out", "/concurrent-strong-probe");

        var responses = await Task.WhenAll(
            scope.Client.PostAsJsonAsync("/api/v1/incidents", envelope, TestContext.Current.CancellationToken),
            scope.Client.PostAsJsonAsync("/api/v1/incidents", envelope, TestContext.Current.CancellationToken));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
        var bodies = await Task.WhenAll(responses.Select(
            response => response.Content.ReadFromJsonAsync<IngestionSignalResponse>(TestContext.Current.CancellationToken)));

        var faultIds = bodies.Select(body => body!.FaultId).Distinct().ToArray();
        Assert.Single(faultIds);

        var jobCount = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_jobs WHERE fault_id = @fault_id;",
            ("fault_id", faultIds[0]));
        Assert.Equal(1, jobCount);
    }

    [DockerAvailableFact]
    public async Task IngestSignal_WeakSignalsWithoutServiceName_EachOpenOwnFaultWithNullIsMassIssue()
    {
        using var scope = await CreateScopeAsync(postgres);

        var first = await PostIngestAsync(scope.Client, UserReportEnvelope("Site is down, checkout does nothing (probe A)"));
        var second = await PostIngestAsync(scope.Client, UserReportEnvelope("Site is down, checkout does nothing (probe B)"));

        Assert.NotEqual(first.FaultId, second.FaultId);

        foreach (var ingested in new[] { first, second })
        {
            var faultResponse = await scope.Client.GetAsync($"/api/v1/faults/{ingested.FaultId}", TestContext.Current.CancellationToken);
            var fault = await faultResponse.Content.ReadFromJsonAsync<IngestionFaultDetails>(TestContext.Current.CancellationToken);
            Assert.NotNull(fault);
            Assert.Equal("Weak", fault.FingerprintStrength);

            var isMassIssue = await ScalarOrNullAsync<string>(
                scope.ConnectionString,
                "SELECT redacted_payload->>'isMassIssue' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet';",
                ("job_id", ingested.JobId!.Value));
            Assert.Null(isMassIssue);
        }
    }

    [DockerAvailableFact]
    public async Task IngestSignal_NeighborCountClearsThreshold_MarksNeighborSetArtifactAsMassIssue()
    {
        using var scope = await CreateScopeAsync(postgres);
        var envelope = TesterEnvelope("mass-issue-probe-svc", "prod", "TimeoutException", "Mass issue probe timed out", "/mass-issue-probe");

        // Every neighbor signal goes through the real ingestion pipeline (normalize/redact/
        // fingerprint via live POSTs), not a hand-seeded SQL row. The first POST opens the fault;
        // the next 5 attach to that same open fault and refresh the existing job-level NeighborSet.
        var first = await PostIngestAsync(scope.Client, envelope);
        for (var index = 0; index < 5; index++)
        {
            var attached = await PostIngestAsync(scope.Client, envelope);
            Assert.Equal(first.FaultId, attached.FaultId);
            Assert.False(attached.IsNewJob);
        }

        var openFaultIsMassIssue = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT redacted_payload->>'isMassIssue' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet';",
            ("job_id", first.JobId!.Value));
        Assert.Equal("true", openFaultIsMassIssue);

        var openFaultNeighborCount = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT redacted_payload->>'neighborCount' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet';",
            ("job_id", first.JobId.Value));
        Assert.Equal("6", openFaultNeighborCount);

        var openFaultNeighborRows = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet';",
            ("job_id", first.JobId.Value));
        Assert.Equal(1, openFaultNeighborRows);

        // Intake has no "close a fault" mechanism of its own; report publication owns that
        // transition -- simulate it directly, backdating well past the configured silence window so
        // the next matching signal opens a recurrence fault+job whose NeighborSet counts all 6 real
        // signals above plus itself. completed_at_utc must stay >= created_at_utc (faults_check), so
        // back-date both together.
        await ExecuteAsync(
            scope.ConnectionString,
            "UPDATE incidentcompass.faults SET created_at_utc = now() - interval '2 days', completed_at_utc = now() - interval '1 day', status = 'Completed' WHERE id = @id;",
            ("id", first.FaultId));

        var recurrence = await PostIngestAsync(scope.Client, envelope);
        Assert.NotEqual(first.FaultId, recurrence.FaultId);
        Assert.True(recurrence.IsNewJob);

        var isMassIssue = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT redacted_payload->>'isMassIssue' FROM incidentcompass.triage_artifacts WHERE job_id = @job_id AND kind = 'NeighborSet';",
            ("job_id", recurrence.JobId!.Value));
        Assert.Equal("true", isMassIssue);

        var recurrenceFaultResponse = await scope.Client.GetAsync($"/api/v1/faults/{recurrence.FaultId}", TestContext.Current.CancellationToken);
        var recurrenceFault = await recurrenceFaultResponse.Content.ReadFromJsonAsync<IngestionFaultDetails>(TestContext.Current.CancellationToken);
        Assert.NotNull(recurrenceFault);
        Assert.Equal(first.FaultId, recurrenceFault.RecurrenceOf);
    }
}
