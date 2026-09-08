using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class ModelCallAccountingRecoveryTests(PostgresRepositoryFixture postgres)
{
    private const string WorkerId = "accounting-recovery-worker";

    [DockerAvailableFact]
    public Task BatchFailsBeforeCommit_RunnerRecoversSuccessAccountingAndFiniteDisposition() =>
        VerifyRecoveryAsync(commitBeforeFailure: false);

    [DockerAvailableFact]
    public Task BatchCommitsThenThrows_RunnerDeduplicatesSuccessAccountingAndFiniteDisposition() =>
        VerifyRecoveryAsync(commitBeforeFailure: true);

    private async Task VerifyRecoveryAsync(bool commitBeforeFailure)
    {
        await using var database = await ActionApprovalDatabase.CreateAsync(postgres);
        var origin = await ActionApprovalTestSupport.SeedOriginAsync(
            database.ConnectionString,
            tenantId: "tenant-accounting-recovery");
        var (job, _) = await ActionApprovalTestSupport.SeedSuccessorJobAsync(
            database.ConnectionString,
            origin,
            WorkerId);
        using var services = ActionApprovalTestSupport.CreateServices(database.ConnectionString);
        var runtimeRepository = services.GetRequiredService<ITriageJobRuntimeRepository>();
        var innerWriter = services.GetRequiredService<ITriageLedgerWriter>();
        ITriageLedgerWriter writer = commitBeforeFailure
            ? new CommitThenThrowLedgerWriter(innerWriter)
            : new ThrowBeforeCommitLedgerWriter();
        var model = new CountingSuccessfulModelClient();
        var tracker = new CountingProviderOutageTracker();
        var configuration = CreateConfiguration(job.ConfigHash);
        var caller = new InvestigationModelCaller(
            model,
            services.GetRequiredService<ITriageLedgerReader>(),
            new TriageLedgerAppender(writer),
            TimeProvider.System,
            tracker);
        var runner = new TriageJobRunner(
            runtimeRepository,
            new StaticConfigurationRepository(configuration),
            new SingleModelCallProcessor(caller),
            TimeProvider.System,
            tracker);

        await runner.ProcessClaimedAsync(
            job,
            WorkerId,
            new TriageJobProcessingSettings(MaxAttempts: 3, RetryDelay: TimeSpan.FromMinutes(1)),
            TestContext.Current.CancellationToken);

        var rows = await ReadAccountingRowsAsync(database.ConnectionString, job.Id);
        var modelCall = Assert.Single(rows, row => row.EventType == "ModelCall");
        var charge = Assert.Single(rows, row => row.EventType == "BudgetEvent");
        using var metadata = JsonDocument.Parse(modelCall.Rationale!);
        var callId = metadata.RootElement.GetProperty("callId").GetGuid();

        Assert.Equal("success", metadata.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, metadata.RootElement.GetProperty("errorCode").ValueKind);
        Assert.Equal("test-provider", metadata.RootElement.GetProperty("provider").GetString());
        Assert.Equal("returned-model", metadata.RootElement.GetProperty("model").GetString());
        Assert.Equal(10, metadata.RootElement.GetProperty("inputTokens").GetInt32());
        Assert.Equal(20, metadata.RootElement.GetProperty("outputTokens").GetInt32());
        Assert.Equal(30, metadata.RootElement.GetProperty("totalTokens").GetInt32());
        Assert.Equal($"model-call:{callId:N}", modelCall.PayloadRef);
        Assert.Equal(modelCall.PayloadRef, charge.PayloadRef);
        Assert.Equal(30, charge.TokensDelta);
        Assert.Equal(1, model.CallCount);
        Assert.Equal(0, tracker.ProviderFailureCount);
        Assert.Equal(0, tracker.ProviderSuccessCount);

        var storedJob = await ReadJobAsync(database.ConnectionString, job.Id);
        Assert.Equal("RetryPending", storedJob.Status);
        Assert.Equal("triage_job_attempt_failed", storedJob.LastErrorCode);
        Assert.False(storedJob.RetryWithoutConsumingAttempt);
    }

    private static TriageConfiguration CreateConfiguration(string configHash) =>
        new(
            configHash,
            new Dictionary<string, TriageProviderSettings>
            {
                ["mock"] = new("Mock", Endpoint: null, ApiKeySecretRef: null)
            },
            new Dictionary<string, TriageRouteSettings>
            {
                ["analysis-chat"] = new(
                    "Chat",
                    "mock",
                    "configured-model",
                    Temperature: 0,
                    MaxOutputTokens: 100,
                    ContextWindowTokens: 8192)
            },
            new OrchestratorSettings(
                "Investigate once.",
                "analysis-chat",
                [],
                new OrchestratorBudgetSettings(
                    MaxWorkers: 1,
                    MaxTokens: 1000,
                    MaxWallClockSeconds: 60,
                    MaxReprompts: 0)),
            new Dictionary<string, TriageRoleSettings>(),
            new Dictionary<string, TriageToolSettings>(),
            [],
            new IngestionSettings("tenant-accounting-recovery", ["tester"]),
            new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
            RedactionSettings.Default);

    private static async Task<IReadOnlyList<AccountingRow>> ReadAccountingRowsAsync(
        string connectionString,
        Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT event_type, rationale, tokens_delta, payload_ref
            FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id
              AND event_type IN ('ModelCall', 'BudgetEvent')
            ORDER BY id;
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        var rows = new List<AccountingRow>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(new AccountingRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return rows;
    }

    private static async Task<JobRow> ReadJobAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT status, last_error_code, retry_without_consuming_attempt
            FROM incidentcompass.triage_jobs
            WHERE id = @job_id;
            """, connection);
        command.Parameters.AddWithValue("job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new JobRow(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetBoolean(2));
    }

    private sealed class SingleModelCallProcessor(InvestigationModelCaller caller) : IClaimedTriageJobProcessor
    {
        public async Task ProcessAsync(
            TriageJob job,
            TriageConfiguration configuration,
            string workerId,
            CancellationToken cancellationToken)
        {
            await caller.CompleteAsync(
                new TriageJobCallContext(
                    job,
                    configuration,
                    TimeProvider.System.GetUtcNow(),
                    "analysis-chat",
                    "worker",
                    "analysis"),
                configuration.Routes["analysis-chat"],
                [new AiChatMessage(AiMessageRole.User, "Perform one bounded analysis call.")],
                tools: null,
                cancellationToken);
        }
    }

    private sealed class CountingSuccessfulModelClient : IAiModelClient
    {
        public int CallCount { get; private set; }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new AiModelResponse(
                "Completed.",
                "returned-model",
                "test-provider",
                new AiModelUsage(10, 20, 30),
                request.CorrelationId,
                []));
        }
    }

    private sealed class StaticConfigurationRepository(TriageConfiguration configuration)
        : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(configuration);

        public Task<TriageConfiguration> GetByHashAsync(
            string configHash,
            CancellationToken cancellationToken) =>
            Task.FromResult(configuration);
    }

    private sealed class ThrowBeforeCommitLedgerWriter : ITriageLedgerWriter
    {
        public Task<TriageLedgerEntry> AppendAsync(
            TriageLedgerAppendRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
            IReadOnlyList<TriageLedgerAppendRequest> requests,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Injected failure before ledger commit.");
    }

    private sealed class CommitThenThrowLedgerWriter(ITriageLedgerWriter inner) : ITriageLedgerWriter
    {
        public Task<TriageLedgerEntry> AppendAsync(
            TriageLedgerAppendRequest request,
            CancellationToken cancellationToken) =>
            inner.AppendAsync(request, cancellationToken);

        public async Task<IReadOnlyList<TriageLedgerEntry>> AppendBatchAsync(
            IReadOnlyList<TriageLedgerAppendRequest> requests,
            CancellationToken cancellationToken)
        {
            await inner.AppendBatchAsync(requests, cancellationToken);
            throw new InvalidOperationException("Injected failure after ledger commit.");
        }
    }

    private sealed class CountingProviderOutageTracker : IProviderOutageTracker
    {
        public int ProviderFailureCount { get; private set; }

        public int ProviderSuccessCount { get; private set; }

        public bool IsBackpressured => false;

        public TimeSpan RetryDelay => TimeSpan.FromSeconds(1);

        public void RecordProviderFailure() => ProviderFailureCount++;

        public void RecordProviderSuccess() => ProviderSuccessCount++;
    }

    private sealed record AccountingRow(
        string EventType,
        string? Rationale,
        int? TokensDelta,
        string? PayloadRef);

    private sealed record JobRow(
        string Status,
        string? LastErrorCode,
        bool RetryWithoutConsumingAttempt);
}
