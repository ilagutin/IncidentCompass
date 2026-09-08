using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.FaultGrouping;
using IncidentCompass.Domain.Incidents.Statuses;
using IncidentCompass.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresTriageLedgerWriterTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task TriageLedgerSchema_UsesIdentityForDbAssignedOrder()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);

        var identityKind = await ScalarAsync<string>(
            connectionString,
            """
            SELECT attidentity::text
            FROM pg_attribute
            WHERE attrelid = 'incidentcompass.triage_ledger'::regclass
              AND attname = 'id';
            """);
        var hasJobOrderIndex = await ScalarAsync<bool>(
            connectionString,
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_indexes
                WHERE schemaname = 'incidentcompass'
                  AND tablename = 'triage_ledger'
                  AND indexname = 'ix_triage_ledger_job_order'
                  AND indexdef LIKE '%(job_id, id)%'
            );
            """);
        var hasToolStatusColumn = await ScalarAsync<bool>(
            connectionString,
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'incidentcompass'
                  AND table_name = 'triage_ledger'
                  AND column_name = 'tool_status'
            );
            """);
        var budgetDeltaColumns = await ScalarAsync<long>(
            connectionString,
            """
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = 'incidentcompass'
              AND table_name = 'triage_ledger'
              AND column_name IN ('tokens_delta', 'workers_delta');
            """);

        Assert.Equal("a", identityKind);
        Assert.True(hasToolStatusColumn);
        Assert.Equal(2, budgetDeltaColumns);
        Assert.True(hasJobOrderIndex);
    }

    [DockerAvailableFact]
    public async Task AppendAsync_AppendsDbOrderedRowsWithConfigHash()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "ledger-order-test");
        var writer = scope.Services.GetRequiredService<ITriageLedgerWriter>();

        var delegated = await writer.AppendAsync(
            new TriageLedgerAppendRequest(
                seed.FaultId,
                seed.JobId,
                Attempt: 1,
                TriageLedgerEventType.Delegated,
                Role: "analysis",
                ToolName: "delegate",
                Rationale: "Need scoped analysis.",
                Decision: null,
                DecisionReason: null,
                PayloadRef: "delegation:analysis:1",
                seed.ConfigHash),
            TestContext.Current.CancellationToken);
        var completed = await writer.AppendAsync(
            new TriageLedgerAppendRequest(
                seed.FaultId,
                seed.JobId,
                Attempt: 1,
                TriageLedgerEventType.WorkerCompleted,
                Role: "analysis",
                ToolName: null,
                Rationale: null,
                Decision: null,
                DecisionReason: null,
                PayloadRef: "artifact:00000000-0000-0000-0000-000000000001",
                seed.ConfigHash),
            TestContext.Current.CancellationToken);
        var toolResult = await writer.AppendAsync(
            new TriageLedgerAppendRequest(
                seed.FaultId,
                seed.JobId,
                Attempt: 1,
                TriageLedgerEventType.ToolResult,
                Role: "analysis",
                ToolName: "tool_x",
                Rationale: "Tool completed successfully.",
                Decision: null,
                DecisionReason: null,
                PayloadRef: "artifact:00000000-0000-0000-0000-000000000002",
                seed.ConfigHash,
                TriageLedgerToolStatus.Succeeded),
            TestContext.Current.CancellationToken);
        var budgetEvent = await writer.AppendAsync(
            new TriageLedgerAppendRequest(
                seed.FaultId,
                seed.JobId,
                Attempt: 1,
                TriageLedgerEventType.BudgetEvent,
                Role: null,
                ToolName: null,
                Rationale: "model_call_charged: charged model tokens to the attempt budget.",
                Decision: null,
                DecisionReason: null,
                PayloadRef: null,
                seed.ConfigHash,
                ToolStatus: null,
                TokensDelta: 42,
                WorkersDelta: 1),
            TestContext.Current.CancellationToken);

        var rows = await ReadLedgerRowsAsync(scope.ConnectionString, seed.JobId);

        Assert.True(delegated.Id > 0);
        Assert.True(completed.Id > delegated.Id);
        Assert.True(toolResult.Id > completed.Id);
        Assert.True(budgetEvent.Id > toolResult.Id);
        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal(delegated.Id, row.Id);
                Assert.Equal("Delegated", row.EventType);
                Assert.Equal("analysis", row.Role);
                Assert.Equal("delegate", row.ToolName);
                Assert.Null(row.ToolStatus);
                Assert.Null(row.TokensDelta);
                Assert.Null(row.WorkersDelta);
                Assert.Equal(seed.ConfigHash, row.ConfigHash);
            },
            row =>
            {
                Assert.Equal(completed.Id, row.Id);
                Assert.Equal("WorkerCompleted", row.EventType);
                Assert.Equal("analysis", row.Role);
                Assert.Null(row.ToolStatus);
                Assert.Null(row.TokensDelta);
                Assert.Null(row.WorkersDelta);
                Assert.Equal(seed.ConfigHash, row.ConfigHash);
            },
            row =>
            {
                Assert.Equal(toolResult.Id, row.Id);
                Assert.Equal("ToolResult", row.EventType);
                Assert.Equal("analysis", row.Role);
                Assert.Equal("tool_x", row.ToolName);
                Assert.Equal("Succeeded", row.ToolStatus);
                Assert.Null(row.TokensDelta);
                Assert.Null(row.WorkersDelta);
                Assert.Equal(seed.ConfigHash, row.ConfigHash);
            },
            row =>
            {
                Assert.Equal(budgetEvent.Id, row.Id);
                Assert.Equal("BudgetEvent", row.EventType);
                Assert.Null(row.Role);
                Assert.Null(row.ToolName);
                Assert.Null(row.ToolStatus);
                Assert.Equal(42, row.TokensDelta);
                Assert.Equal(1, row.WorkersDelta);
                Assert.Equal(seed.ConfigHash, row.ConfigHash);
            });
    }

    [DockerAvailableFact]
    public async Task AppendBatchAsync_RollsBackModelCallWhenChargeInsertFails()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "ledger-batch-rollback");
        var writer = scope.Services.GetRequiredService<ITriageLedgerWriter>();
        var payloadRef = "model-call:" + Guid.NewGuid().ToString("N");
        var suffix = Guid.NewGuid().ToString("N");
        var functionName = "fail_batch_budget_" + suffix;
        var triggerName = "fail_batch_budget_" + suffix;
        await ExecuteAsync(scope.ConnectionString, $"""
            CREATE FUNCTION incidentcompass.{functionName}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.payload_ref = '{payloadRef}' AND NEW.event_type = 'BudgetEvent' THEN
                    RAISE EXCEPTION 'injected batch failure';
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
            await Assert.ThrowsAnyAsync<Exception>(() => writer.AppendBatchAsync(
            [
                new TriageLedgerAppendRequest(
                    seed.FaultId, seed.JobId, 1, TriageLedgerEventType.ModelCall,
                    "analysis", null, "{}", null, null, payloadRef, seed.ConfigHash),
                new TriageLedgerAppendRequest(
                    seed.FaultId, seed.JobId, 1, TriageLedgerEventType.BudgetEvent,
                    null, null, "model_call_charged", null, null, payloadRef, seed.ConfigHash,
                    TokensDelta: 7)
            ],
                TestContext.Current.CancellationToken));
        }
        finally
        {
            await ExecuteAsync(scope.ConnectionString, $"""
                DROP TRIGGER {triggerName} ON incidentcompass.triage_ledger;
                DROP FUNCTION incidentcompass.{functionName}();
                """);
        }

        Assert.Equal(0, await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT count(*) FROM incidentcompass.triage_ledger WHERE job_id = @job_id AND payload_ref = @payload_ref;",
            ("job_id", seed.JobId),
            ("payload_ref", payloadRef)));
    }

    [DockerAvailableFact]
    public async Task AppendAsync_CommitsOutsideAmbientIntakeTransaction()
    {
        using var scope = await CreateScopeAsync();
        var seed = await SeedJobAsync(scope.ConnectionString, "ledger-own-commit-test");
        var writer = scope.Services.GetRequiredService<ITriageLedgerWriter>();
        var unitOfWork = scope.Services.GetRequiredService<IIntakeUnitOfWork>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unitOfWork.ExecuteAsync<int>(
                async cancellationToken =>
                {
                    await writer.AppendAsync(
                        new TriageLedgerAppendRequest(
                            seed.FaultId,
                            seed.JobId,
                            Attempt: 1,
                            TriageLedgerEventType.Delegated,
                            Role: "analysis",
                            ToolName: "delegate",
                            Rationale: "This event must survive the outer rollback.",
                            Decision: null,
                            DecisionReason: null,
                            PayloadRef: "delegation:analysis:rollback-probe",
                            seed.ConfigHash),
                        cancellationToken);

                    throw new InvalidOperationException("Force ambient intake rollback.");
                },
                TestContext.Current.CancellationToken));

        var count = await ScalarAsync<long>(
            scope.ConnectionString,
            """
            SELECT COUNT(*)
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id
              AND event_type = 'Delegated'
              AND payload_ref = 'delegation:analysis:rollback-probe';
            """,
            ("job_id", seed.JobId));

        Assert.Equal(1, count);
    }

    private async Task<RepositoryScope> CreateScopeAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
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
        var serviceProvider = services.BuildServiceProvider();

        return new RepositoryScope(serviceProvider, connectionString);
    }

    private static async Task<JobSeed> SeedJobAsync(string connectionString, string configHashPrefix)
    {
        var signalId = Guid.NewGuid();
        var faultId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var unique = Guid.NewGuid().ToString("N");
        var configHash = configHashPrefix + "-" + unique;
        var fingerprint = "ledger-fingerprint-" + unique;
        var serviceName = "ledger-service-" + unique;

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
                NULL, false, NULL, NULL, NULL, NULL, NULL, @service_name, 'prod', 'POST /ledger',
                'warning', 'LedgerProbe', 'ledger probe', 'Ledger probe', NULL, 'POST', '/ledger',
                500, 1000, '{}'::jsonb, '{}'::jsonb, now(), now());

            INSERT INTO incidentcompass.faults (
                id, trigger_signal_id, tenant_id, status, fingerprint, fingerprint_version,
                fingerprint_strength, service_name, environment, severity, correlation_id,
                created_at_utc, completed_at_utc, recurrence_of)
            VALUES (
                @fault_id, @signal_id, 'local', 'Queued', @fingerprint, 1,
                'strong', @service_name, 'prod', 'warning', NULL, now(), NULL, NULL);

            UPDATE incidentcompass.signals
            SET fault_id = @fault_id
            WHERE id = @signal_id;

            INSERT INTO incidentcompass.triage_jobs (
                id, fault_id, status, attempt, locked_by, locked_until_utc, next_attempt_at_utc,
                last_error_code, last_error_message, config_hash, created_at_utc, updated_at_utc)
            VALUES (
                @job_id, @fault_id, 'Pending', 1, NULL, NULL, NULL,
                NULL, NULL, @config_hash, now(), now());
            """,
            ("config_hash", configHash),
            ("fingerprint", fingerprint),
            ("service_name", serviceName),
            ("signal_id", signalId),
            ("fault_id", faultId),
            ("job_id", jobId));

        return new JobSeed(faultId, jobId, configHash);
    }

    private static async Task<IReadOnlyList<LedgerRow>> ReadLedgerRowsAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT id, event_type, role, tool_name, config_hash, tool_status, tokens_delta, workers_delta
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id
            ORDER BY id;
            """,
            connection);
        command.Parameters.AddWithValue("job_id", jobId);

        var rows = new List<LedgerRow>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new LedgerRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7)));
        }

        return rows;
    }

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

    private sealed record LedgerRow(
        long Id,
        string EventType,
        string? Role,
        string? ToolName,
        string ConfigHash,
        string? ToolStatus,
        int? TokensDelta,
        int? WorkersDelta);
}
