using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresTriageJobRuntimeRepositoryTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task ClaimNextAsync_PendingJob_ClaimsAndMarksFaultAnalyzing()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "claim-pending", "Pending");
        var repository = scope.Services.GetRequiredService<ITriageJobRuntimeRepository>();

        var claimed = await repository.ClaimNextAsync(
            "worker-a",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        Assert.NotNull(claimed);
        Assert.Equal(seed.JobId, claimed.Id);
        Assert.Equal(TriageJobStatus.Processing, claimed.Status);
        Assert.Equal(1, claimed.Attempt);
        Assert.Equal("worker-a", claimed.LockedBy);
        Assert.Equal(seed.ConfigHash, claimed.ConfigHash);
        Assert.NotNull(claimed.LockedUntilUtc);

        var faultStatus = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT status FROM incidentcompass.faults WHERE id = @fault_id;",
            ("fault_id", seed.FaultId));
        Assert.Equal("Analyzing", faultStatus);
    }

    [DockerAvailableFact]
    public async Task ClaimNextAsync_ExpiredProcessingJob_ReclaimsAsNextAttempt()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "claim-expired", "Processing");
        await ExecuteAsync(
            scope.ConnectionString,
            """
            UPDATE incidentcompass.triage_jobs
            SET locked_by = 'old-worker', locked_until_utc = now() - interval '1 minute'
            WHERE id = @job_id;
            """,
            ("job_id", seed.JobId));
        var repository = scope.Services.GetRequiredService<ITriageJobRuntimeRepository>();

        var claimed = await repository.ClaimNextAsync(
            "worker-b",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        Assert.NotNull(claimed);
        Assert.Equal(seed.JobId, claimed.Id);
        Assert.Equal(2, claimed.Attempt);
        Assert.Equal("worker-b", claimed.LockedBy);
    }


    [DockerAvailableFact]
    public async Task RenewLeaseAsync_ExtendsOnlyTheCurrentUnexpiredOwnerLease()
    {
        // ClaimNextAsync/RenewAsync compute "now" and lease expiry from the injected TimeProvider
        // rather than SQL now(), so a fake clock lets this test advance virtual time deterministically
        // instead of sleeping for real wall-clock milliseconds.
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var scope = await CreateScopeAsync(timeProvider);
        var seed = await SeedJobAsync(scope.ConnectionString, "renew-lease", "Pending");
        var repository = scope.Services.GetRequiredService<ITriageJobRuntimeRepository>();
        var claimed = await repository.ClaimNextAsync(
            "worker-renew-a",
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);

        timeProvider.Advance(TimeSpan.FromMilliseconds(400));
        Assert.True(await repository.RenewLeaseAsync(
            claimed,
            "worker-renew-a",
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken));

        timeProvider.Advance(TimeSpan.FromMilliseconds(700));
        Assert.Null(await repository.ClaimNextAsync(
            "worker-renew-b",
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken));

        await ExecuteAsync(
            scope.ConnectionString,
            """
            UPDATE incidentcompass.triage_jobs
            SET attempt = @attempt, locked_by = 'worker-renew-b', locked_until_utc = now() + interval '1 minute'
            WHERE id = @job_id;
            """,
            ("attempt", claimed.Attempt + 1),
            ("job_id", claimed.Id));

        Assert.False(await repository.RenewLeaseAsync(
            claimed,
            "worker-renew-a",
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken));
    }
    [DockerAvailableFact]
    public async Task ClaimNextAsync_RetryPendingHonorsNextAttemptTime()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "claim-retry", "RetryPending");
        await ExecuteAsync(
            scope.ConnectionString,
            """
            UPDATE incidentcompass.triage_jobs
            SET next_attempt_at_utc = now() + interval '1 hour'
            WHERE id = @job_id;
            """,
            ("job_id", seed.JobId));
        var repository = scope.Services.GetRequiredService<ITriageJobRuntimeRepository>();

        var notDue = await repository.ClaimNextAsync(
            "worker-c",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.Null(notDue);

        await ExecuteAsync(
            scope.ConnectionString,
            """
            UPDATE incidentcompass.triage_jobs
            SET next_attempt_at_utc = now() - interval '1 minute'
            WHERE id = @job_id;
            """,
            ("job_id", seed.JobId));

        var claimed = await repository.ClaimNextAsync(
            "worker-c",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        Assert.NotNull(claimed);
        Assert.Equal(2, claimed.Attempt);
        Assert.Equal("worker-c", claimed.LockedBy);
    }

    [DockerAvailableFact]
    public async Task ClaimNextAsync_ProviderOutageRetriesDoNotConsumeOrdinaryAttemptBudget()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "claim-provider-outage", "Pending");
        var repository = scope.Services.GetRequiredService<ITriageJobRuntimeRepository>();

        var firstClaim = await repository.ClaimNextAsync(
            "worker-provider-outage",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(firstClaim);
        Assert.Equal(1, firstClaim.Attempt);

        await RecordProviderOutageAsync(repository, firstClaim);
        var secondClaim = await repository.ClaimNextAsync(
            "worker-provider-outage",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(secondClaim);
        Assert.Equal(1, secondClaim.Attempt);
        Assert.Null(secondClaim.LastErrorCode);

        await RecordProviderOutageAsync(repository, secondClaim);
        var firstOrdinaryAttempt = await repository.ClaimNextAsync(
            "worker-provider-outage",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(firstOrdinaryAttempt);
        Assert.Equal(1, firstOrdinaryAttempt.Attempt);

        await repository.RecordAttemptFailureAsync(
            firstOrdinaryAttempt,
            "worker-provider-outage",
            new TriageJobAttemptFailure(
                TriageJobStatus.RetryPending,
                "provider_unavailable",
                "ordinary failure with a provider-like error code",
                DateTimeOffset.UtcNow.AddMinutes(-1)),
            TestContext.Current.CancellationToken);

        var secondOrdinaryAttempt = await repository.ClaimNextAsync(
            "worker-provider-outage",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(secondOrdinaryAttempt);
        Assert.Equal(2, secondOrdinaryAttempt.Attempt);
    }
    [DockerAvailableFact]
    public async Task RecordAttemptFailureAsync_RetryAndDeadLetterTransitionsAreFenced()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "claim-failure", "Pending");
        var repository = scope.Services.GetRequiredService<ITriageJobRuntimeRepository>();
        var claimed = await repository.ClaimNextAsync(
            "worker-d",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);

        await repository.RecordAttemptFailureAsync(
            claimed,
            "worker-d",
            new TriageJobAttemptFailure(
                TriageJobStatus.RetryPending,
                "test_retry",
                "retry this attempt",
                DateTimeOffset.UtcNow.AddMinutes(1)),
            TestContext.Current.CancellationToken);

        var retryRow = await ReadJobStateAsync(scope.ConnectionString, seed.JobId);
        Assert.Equal("RetryPending", retryRow.Status);
        Assert.Null(retryRow.LockedBy);
        Assert.NotNull(retryRow.NextAttemptAtUtc);
        Assert.Equal("test_retry", retryRow.LastErrorCode);

        await ExecuteAsync(
            scope.ConnectionString,
            """
            UPDATE incidentcompass.triage_jobs
            SET status = 'Processing', attempt = 2, locked_by = 'worker-d', locked_until_utc = now() + interval '5 minutes'
            WHERE id = @job_id;
            """,
            ("job_id", seed.JobId));
        var secondAttempt = claimed with { Attempt = 2, LockedBy = "worker-d" };

        await repository.RecordAttemptFailureAsync(
            secondAttempt,
            "worker-d",
            new TriageJobAttemptFailure(
                TriageJobStatus.DeadLettered,
                "test_deadletter",
                "dead letter this job",
                NextAttemptAtUtc: null),
            TestContext.Current.CancellationToken);

        var deadLetteredRow = await ReadJobStateAsync(scope.ConnectionString, seed.JobId);
        Assert.Equal("DeadLettered", deadLetteredRow.Status);
        Assert.Null(deadLetteredRow.LockedBy);
        Assert.Null(deadLetteredRow.NextAttemptAtUtc);
        Assert.Equal("test_deadletter", deadLetteredRow.LastErrorCode);

        var faultStatus = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT status FROM incidentcompass.faults WHERE id = @fault_id;",
            ("fault_id", seed.FaultId));
        Assert.Equal("Failed", faultStatus);
    }

    [DockerAvailableFact]
    public async Task RecordAttemptFailureAsync_StaleAttemptDoesNotMutateJobOrFault()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "claim-failure-stale", "Pending");
        var repository = scope.Services.GetRequiredService<ITriageJobRuntimeRepository>();
        var claimed = await repository.ClaimNextAsync(
            "worker-stale-failure",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);

        await repository.RecordAttemptFailureAsync(
            claimed with { Attempt = claimed.Attempt + 1 },
            "worker-stale-failure",
            new TriageJobAttemptFailure(
                TriageJobStatus.DeadLettered,
                "stale_deadletter",
                "stale attempts must not update",
                NextAttemptAtUtc: null),
            TestContext.Current.CancellationToken);

        var row = await ReadJobStateAsync(scope.ConnectionString, seed.JobId);
        Assert.Equal("Processing", row.Status);
        Assert.Equal("worker-stale-failure", row.LockedBy);
        Assert.Null(row.NextAttemptAtUtc);
        Assert.Null(row.LastErrorCode);

        var faultStatus = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT status FROM incidentcompass.faults WHERE id = @fault_id;",
            ("fault_id", seed.FaultId));
        Assert.Equal("Analyzing", faultStatus);
    }

    [DockerAvailableFact]
    public async Task RecordAttemptFailureAsync_KnownFailedUsageAndDispositionCommitOnce()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "failed-usage-known", "Pending");
        var repository = scope.Services.GetRequiredService<ITriageJobRuntimeRepository>();
        var claimed = await repository.ClaimNextAsync(
            "worker-failed-usage",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        var accounting = CreateAccounting(3596);
        var failure = new TriageJobAttemptFailure(
            TriageJobStatus.DeadLettered,
            "provider_output_limit_reached",
            "provider_output_limit_reached: InvestigationModelCallFailureException.",
            NextAttemptAtUtc: null,
            ModelCallAccounting: accounting);

        await repository.RecordAttemptFailureAsync(
            claimed,
            "worker-failed-usage",
            failure,
            TestContext.Current.CancellationToken);
        await repository.RecordAttemptFailureAsync(
            claimed,
            "worker-failed-usage",
            failure,
            TestContext.Current.CancellationToken);

        var modelCalls = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT count(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'ModelCall' AND payload_ref = @payload_ref;",
            ("job_id", seed.JobId),
            ("payload_ref", accounting.PayloadRef));
        var charges = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT count(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'BudgetEvent' AND payload_ref = @payload_ref AND tokens_delta = 3596;",
            ("job_id", seed.JobId),
            ("payload_ref", accounting.PayloadRef));
        var row = await ReadJobStateAsync(scope.ConnectionString, seed.JobId);

        Assert.Equal(1, modelCalls);
        Assert.Equal(1, charges);
        Assert.Equal("DeadLettered", row.Status);
        Assert.Equal("provider_output_limit_reached", row.LastErrorCode);
    }

    [DockerAvailableFact]
    public async Task RecordAttemptFailureAsync_ZeroIsChargedButUnknownUsageIsNot()
    {
        using var scope = await CreateScopeAsync();
        var repository = scope.Services.GetRequiredService<ITriageJobRuntimeRepository>();
        var zeroSeed = await SeedJobAsync(scope.ConnectionString, "failed-usage-zero", "Pending");
        var zeroClaim = await repository.ClaimNextAsync(
            "worker-zero-usage",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(zeroClaim);
        var zeroAccounting = CreateAccounting(0);
        await repository.RecordAttemptFailureAsync(
            zeroClaim,
            "worker-zero-usage",
            CreateRetryFailure(zeroAccounting),
            TestContext.Current.CancellationToken);

        var unknownSeed = await SeedJobAsync(scope.ConnectionString, "failed-usage-unknown", "Pending");
        var unknownClaim = await repository.ClaimNextAsync(
            "worker-unknown-usage",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(unknownClaim);
        var unknownAccounting = CreateAccounting(totalTokens: null);
        await repository.RecordAttemptFailureAsync(
            unknownClaim,
            "worker-unknown-usage",
            CreateRetryFailure(unknownAccounting),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, await CountAccountingRowsAsync(
            scope.ConnectionString, zeroSeed.JobId, "BudgetEvent", zeroAccounting.PayloadRef));
        Assert.Equal(0, await ScalarAsync<int>(
            scope.ConnectionString,
            "SELECT tokens_delta FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = 'BudgetEvent' AND payload_ref = @payload_ref;",
            ("job_id", zeroSeed.JobId),
            ("payload_ref", zeroAccounting.PayloadRef)));
        Assert.Equal(1, await CountAccountingRowsAsync(
            scope.ConnectionString, unknownSeed.JobId, "ModelCall", unknownAccounting.PayloadRef));
        Assert.Equal(0, await CountAccountingRowsAsync(
            scope.ConnectionString, unknownSeed.JobId, "BudgetEvent", unknownAccounting.PayloadRef));
    }

    [DockerAvailableFact]
    public async Task RecordAttemptFailureAsync_StaleOwnerAccountingPersistsWithoutMutatingCurrentJob()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "failed-usage-stale", "Pending");
        var repository = scope.Services.GetRequiredService<ITriageJobRuntimeRepository>();
        var claimed = await repository.ClaimNextAsync(
            "worker-stale-accounting",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        await ExecuteAsync(
            scope.ConnectionString,
            "UPDATE incidentcompass.triage_jobs SET attempt = 2, locked_by = 'current-owner' WHERE id = @job_id;",
            ("job_id", seed.JobId));
        var accounting = CreateAccounting(17);

        await repository.RecordAttemptFailureAsync(
            claimed,
            "worker-stale-accounting",
            new TriageJobAttemptFailure(
                TriageJobStatus.DeadLettered,
                "provider_output_limit_reached",
                "provider_output_limit_reached: InvestigationModelCallFailureException.",
                NextAttemptAtUtc: null,
                ModelCallAccounting: accounting),
            TestContext.Current.CancellationToken);

        var row = await ReadJobStateAsync(scope.ConnectionString, seed.JobId);
        Assert.Equal("Processing", row.Status);
        Assert.Equal("current-owner", row.LockedBy);
        Assert.Null(row.LastErrorCode);
        Assert.Equal(1, await CountAccountingRowsAsync(
            scope.ConnectionString, seed.JobId, "ModelCall", accounting.PayloadRef));
        Assert.Equal(1, await CountAccountingRowsAsync(
            scope.ConnectionString, seed.JobId, "BudgetEvent", accounting.PayloadRef));
    }

    [DockerAvailableFact]
    public async Task RecordAttemptFailureAsync_LedgerFailureRollsBackAccountingAndDisposition()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "failed-usage-rollback", "Pending");
        var repository = scope.Services.GetRequiredService<ITriageJobRuntimeRepository>();
        var claimed = await repository.ClaimNextAsync(
            "worker-rollback-accounting",
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        var accounting = CreateAccounting(23);
        var suffix = Guid.NewGuid().ToString("N");
        var functionName = "fail_budget_" + suffix;
        var triggerName = "fail_budget_" + suffix;
        await ExecuteAsync(scope.ConnectionString, $"""
            CREATE FUNCTION incidentcompass.{functionName}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.job_id = '{seed.JobId}'::uuid AND NEW.event_type = 'BudgetEvent' THEN
                    RAISE EXCEPTION 'injected budget failure';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER {triggerName}
            BEFORE INSERT ON incidentcompass.triage_ledger
            FOR EACH ROW EXECUTE FUNCTION incidentcompass.{functionName}();
            """);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => repository.RecordAttemptFailureAsync(
                claimed,
                "worker-rollback-accounting",
                CreateRetryFailure(accounting),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            await ExecuteAsync(scope.ConnectionString, $"""
                DROP TRIGGER {triggerName} ON incidentcompass.triage_ledger;
                DROP FUNCTION incidentcompass.{functionName}();
                """);
        }

        Assert.Equal(0, await CountAccountingRowsAsync(
            scope.ConnectionString, seed.JobId, "ModelCall", accounting.PayloadRef));
        var row = await ReadJobStateAsync(scope.ConnectionString, seed.JobId);
        Assert.Equal("Processing", row.Status);
        Assert.Null(row.LastErrorCode);
    }

    [DockerAvailableFact]
    public async Task InvestigationContext_CurrentAttemptExcludesPriorAttemptArtifacts()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "context-attempt", "Pending");
        var jobLevelId = Guid.NewGuid();
        var priorAttemptId = Guid.NewGuid();
        var currentAttemptId = Guid.NewGuid();
        await ExecuteAsync(
            scope.ConnectionString,
            """
            INSERT INTO incidentcompass.triage_artifacts (
                id, job_id, attempt, kind, domain_ref, redacted_payload, content_hash, created_at_utc)
            VALUES
                (@job_level_id, @job_id, NULL, 'TriggerSignal', NULL, '{}'::jsonb, 'job-level', now()),
                (@prior_attempt_id, @job_id, 1, 'WorkerOutput', NULL, '{}'::jsonb, 'prior-attempt', now()),
                (@current_attempt_id, @job_id, 2, 'RetrievedItem', NULL, '{}'::jsonb, 'current-attempt', now());
            """,
            ("job_level_id", jobLevelId),
            ("prior_attempt_id", priorAttemptId),
            ("current_attempt_id", currentAttemptId),
            ("job_id", seed.JobId));
        var repository = scope.Services.GetRequiredService<ITriageJobInvestigationContextRepository>();

        var context = await repository.GetAsync(
            seed.JobId,
            attempt: 2,
            TestContext.Current.CancellationToken);

        Assert.Contains(
            context.JobArtifacts,
            artifact => artifact.Id == jobLevelId && artifact.Attempt is null);
        Assert.Contains(
            context.JobArtifacts,
            artifact => artifact.Id == currentAttemptId && artifact.Attempt == 2);
        Assert.DoesNotContain(
            context.JobArtifacts,
            artifact => artifact.Id == priorAttemptId);
    }

    private async Task<RepositoryScope> CreateScopeAsync(TimeProvider? timeProvider = null)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:IncidentCompass"] = connectionString,
                ["IncidentCompass:Postgres:ConnectionStringName"] = "IncidentCompass"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        if (timeProvider is not null)
        {
            // ClaimNextAsync/RenewAsync compute lease timestamps from the injected TimeProvider
            // (not SQL now()), so a fake clock here lets lease-timing tests advance virtual time
            // instead of sleeping in real wall-clock time. AddSingleton after AddInfrastructure's
            // TryAddSingleton(TimeProvider.System) wins on resolution.
            services.AddSingleton(timeProvider);
        }

        var serviceProvider = services.BuildServiceProvider();

        return new RepositoryScope(serviceProvider, connectionString);
    }

    private static async Task<JobSeed> SeedJobAsync(string connectionString, string prefix, string jobStatus)
    {
        var signalId = Guid.NewGuid();
        var faultId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var unique = Guid.NewGuid().ToString("N");
        var fingerprint = prefix + "-fingerprint-" + unique;
        var serviceName = prefix + "-service-" + unique;
        var configHash = prefix + "-config-" + unique;
        var faultStatus = jobStatus == "Processing" ? "Analyzing" : "Queued";

        await ExecuteAsync(
            connectionString,
            """
            INSERT INTO incidentcompass.triage_config_snapshots (config_hash, serialized_config, instructions, created_at_utc)
            VALUES (@config_hash, '{}'::jsonb, '{}'::jsonb, now());

            INSERT INTO incidentcompass.signals (
                id, tenant_id, source, fault_id, fingerprint, fingerprint_version, fingerprint_strength,
                external_id, is_suppressed, suppressed_by_fault_id, suppression_reason, trace_id, span_id,
                parent_span_id, service_name, environment, operation_name, severity, error_type, error_message,
                summary, description, http_method, http_route, http_status_code, duration_ms, attributes, body,
                observed_at_utc, received_at_utc)
            VALUES (
                @signal_id, 'local', 'tester', NULL, @fingerprint, 1, 'strong',
                NULL, false, NULL, NULL, NULL, NULL, NULL, @service_name, 'prod', 'POST /claim',
                'warning', 'ClaimProbe', 'claim probe', 'Claim probe', NULL, 'POST', '/claim',
                500, 1000, '{}'::jsonb, '{}'::jsonb, now(), now());

            INSERT INTO incidentcompass.faults (
                id, trigger_signal_id, tenant_id, status, fingerprint, fingerprint_version,
                fingerprint_strength, service_name, environment, severity, correlation_id,
                created_at_utc, completed_at_utc, recurrence_of)
            VALUES (
                @fault_id, @signal_id, 'local', @fault_status, @fingerprint, 1,
                'strong', @service_name, 'prod', 'warning', NULL, now(), NULL, NULL);

            UPDATE incidentcompass.signals
            SET fault_id = @fault_id
            WHERE id = @signal_id;

            INSERT INTO incidentcompass.triage_jobs (
                id, fault_id, status, attempt, locked_by, locked_until_utc, next_attempt_at_utc,
                last_error_code, last_error_message, config_hash, created_at_utc, updated_at_utc)
            VALUES (
                @job_id, @fault_id, @job_status, 1, NULL, NULL, NULL,
                NULL, NULL, @config_hash, now(), now());
            """,
            ("config_hash", configHash),
            ("signal_id", signalId),
            ("fault_id", faultId),
            ("job_id", jobId),
            ("fingerprint", fingerprint),
            ("service_name", serviceName),
            ("fault_status", faultStatus),
            ("job_status", jobStatus));

        return new JobSeed(faultId, jobId, configHash);
    }

    private static async Task<JobStateRow> ReadJobStateAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT status, locked_by, next_attempt_at_utc, last_error_code
            FROM incidentcompass.triage_jobs
            WHERE id = @job_id;
            """,
            connection);
        command.Parameters.AddWithValue("job_id", jobId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException("Seeded triage job was not found.");
        }

        return new JobStateRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static Task RecordProviderOutageAsync(ITriageJobRuntimeRepository repository, TriageJob job) =>
        repository.RecordAttemptFailureAsync(
            job,
            "worker-provider-outage",
            new TriageJobAttemptFailure(
                TriageJobStatus.RetryPending,
                "provider_unavailable",
                "Triage delayed: provider unavailable.",
                DateTimeOffset.UtcNow.AddMinutes(-1),
                TriageJobRetryBudgetDisposition.DoNotConsumeAttempt),
            TestContext.Current.CancellationToken);

    private static InvestigationModelCallAccounting CreateAccounting(int? totalTokens)
    {
        var callId = Guid.NewGuid();
        return new InvestigationModelCallAccounting(
            callId,
            Role: "analysis",
            Metadata: new ModelCallLedgerMetadata(
                Kind: "worker",
                RouteId: "analysis-chat",
                Model: "test-model",
                Provider: "test-provider",
                UsageSource: totalTokens is null ? "unknown" : "provider",
                InputTokens: totalTokens is null ? null : 0,
                OutputTokens: totalTokens,
                TotalTokens: totalTokens,
                DurationMs: 100,
                ProposedToolCallCount: 0,
                CallId: callId,
                Outcome: "failed",
                ErrorCode: "provider_output_limit_reached"),
            ChargeTokens: totalTokens);
    }

    private static TriageJobAttemptFailure CreateRetryFailure(InvestigationModelCallAccounting accounting) =>
        new(
            TriageJobStatus.RetryPending,
            "provider_generation_timeout",
            "provider_generation_timeout: InvestigationModelCallFailureException.",
            DateTimeOffset.UtcNow.AddMinutes(1),
            ModelCallAccounting: accounting);

    private static Task<long> CountAccountingRowsAsync(
        string connectionString,
        Guid jobId,
        string eventType,
        string payloadRef) =>
        ScalarAsync<long>(
            connectionString,
            "SELECT count(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND event_type = @event_type AND payload_ref = @payload_ref;",
            ("job_id", jobId),
            ("event_type", eventType),
            ("payload_ref", payloadRef));
    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = await command.ExecuteScalarAsync();
        return (T)result!;
    }

    private sealed record RepositoryScope(ServiceProvider Services, string ConnectionString) : IDisposable
    {
        public void Dispose()
        {
            Services.Dispose();
        }
    }

    private sealed record JobSeed(Guid FaultId, Guid JobId, string ConfigHash);

    private sealed record JobStateRow(string Status, string? LockedBy, DateTime? NextAttemptAtUtc, string? LastErrorCode);
}
