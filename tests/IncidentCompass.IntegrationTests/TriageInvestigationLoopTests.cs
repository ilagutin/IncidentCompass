using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Infrastructure.ModelGateway.Mock;
using IncidentCompass.Worker;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class TriageInvestigationLoopTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_ScriptedMockDelegatesAnalysisAndPublishesMinimalReport()
    {
        using var scope = await CreateScopeAsync();
        var ingested = await PostIngestAsync(scope.Client, TesterEnvelope());
        Assert.NotNull(ingested.JobId);
        Assert.NotNull(ingested.ConfigHash);

        using var serviceScope = scope.Factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var claimed = await runner.ClaimNextAsync("worker-unit4", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);
        Assert.Equal(ingested.JobId.Value, claimed.Id);
        Assert.Equal(ingested.ConfigHash, claimed.ConfigHash);

        await runner.ProcessClaimedAsync(
            claimed,
            "worker-unit4",
            new TriageJobProcessingSettings(MaxAttempts: 3, RetryDelay: TimeSpan.FromSeconds(1)),
            TestContext.Current.CancellationToken);

        var job = await ReadJobAsync(scope.ConnectionString, claimed.Id);
        Assert.Equal("Succeeded", job.Status);
        Assert.Null(job.LockedBy);

        var faultStatus = await ScalarAsync<string>(
            scope.ConnectionString,
            "SELECT status FROM incidentcompass.faults WHERE id = @fault_id;",
            ("fault_id", ingested.FaultId));
        Assert.Equal("Completed", faultStatus);

        var report = await ReadReportAsync(scope.ConnectionString, ingested.FaultId);
        Assert.Equal("Completed", report.Status);
        Assert.Equal("SimpleKnownError", report.Classification);
        Assert.Equal("Medium", report.Confidence);
        Assert.Equal(ingested.ConfigHash, report.ConfigHash);

        var workerArtifact = await ReadWorkerArtifactAsync(scope.ConnectionString, claimed.Id);
        Assert.Equal(claimed.Attempt, workerArtifact.Attempt);
        Assert.Equal("worker:analysis", workerArtifact.DomainRef);
        Assert.Equal("SimpleKnownError", workerArtifact.CandidateClassification);

        var ledgerRows = await ReadLedgerRowsAsync(scope.ConnectionString, claimed.Id);
        Assert.Contains(ledgerRows, row => row.EventType == "ModelCall");
        Assert.Contains(ledgerRows, row => row.EventType == "BudgetEvent");
        var delegated = Assert.Single(ledgerRows, row => row.EventType == "Delegated");
        var completed = Assert.Single(ledgerRows, row => row.EventType == "WorkerCompleted");
        var published = Assert.Single(ledgerRows, row => row.EventType == "ReportPublished");
        AssertLedger(delegated, 0, "Delegated", "analysis", "delegate", ingested.ConfigHash!);
        AssertLedger(completed, 1, "WorkerCompleted", "analysis", null, ingested.ConfigHash!);
        AssertLedger(published, 2, "ReportPublished", null, "publish_report", ingested.ConfigHash!);
        Assert.True(delegated.Id < completed.Id);
        Assert.True(completed.Id < published.Id);
        Assert.DoesNotContain(ledgerRows, row => row.EventType is "ToolProposed" or "PolicyDecision" or "ToolResult");
    }

    [DockerAvailableFact]
    public async Task ProcessClaimedAsync_WorkerCrashAfterDelegationKeepsAlreadyWrittenLedgerEvent()
    {
        using var scope = await CreateScopeAsync(services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddScoped<IAiModelClient, CrashAfterDelegationModelClient>();
        });
        var ingested = await PostIngestAsync(scope.Client, TesterEnvelope());
        Assert.NotNull(ingested.JobId);
        Assert.NotNull(ingested.ConfigHash);

        using var serviceScope = scope.Factory.Services.CreateScope();
        var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var claimed = await runner.ClaimNextAsync("worker-crash", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        Assert.NotNull(claimed);

        await runner.ProcessClaimedAsync(
            claimed,
            "worker-crash",
            new TriageJobProcessingSettings(MaxAttempts: 3, RetryDelay: TimeSpan.FromMinutes(1)),
            TestContext.Current.CancellationToken);

        var job = await ReadJobAsync(scope.ConnectionString, claimed.Id);
        Assert.Equal("RetryPending", job.Status);
        Assert.Null(job.LockedBy);

        var rows = await ReadLedgerRowsAsync(scope.ConnectionString, claimed.Id);
        Assert.Contains(rows, row => row.EventType == "ModelCall");
        Assert.Contains(rows, row => row.EventType == "BudgetEvent");
        var delegated = Assert.Single(rows, row => row.EventType == "Delegated");
        AssertLedger(delegated, 0, "Delegated", "analysis", "delegate", ingested.ConfigHash!);
    }

    [DockerAvailableFact]
    public async Task ProviderOutage_BackpressuresClaimsThenRecoversAndPublishes()
    {
        var modelClient = new ProviderOutageThenSuccessModelClient();
        // ProviderOutageTracker computes its backpressure window from the injected TimeProvider,
        // so a fake clock lets the test fast-forward past the 1-second BackpressureSeconds window
        // deterministically instead of sleeping for real wall-clock seconds.
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var scope = await CreateScopeAsync(services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddSingleton(modelClient);
            services.AddScoped<IAiModelClient>(serviceProvider => serviceProvider.GetRequiredService<ProviderOutageThenSuccessModelClient>());
            services.AddSingleton<TimeProvider>(timeProvider);
        });
        var ingested = await PostIngestAsync(scope.Client, TesterEnvelope());
        Assert.NotNull(ingested.JobId);

        using (var serviceScope = scope.Factory.Services.CreateScope())
        {
            var runner = serviceScope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
            var claimed = await runner.ClaimNextAsync("worker-provider-outage", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            Assert.NotNull(claimed);
            await runner.ProcessClaimedAsync(
                claimed,
                "worker-provider-outage",
                new TriageJobProcessingSettings(MaxAttempts: 1, RetryDelay: TimeSpan.FromSeconds(1)),
                TestContext.Current.CancellationToken);
        }

        var delayed = await ReadJobAsync(scope.ConnectionString, ingested.JobId.Value);
        Assert.Equal("RetryPending", delayed.Status);
        Assert.Equal("provider_unavailable", delayed.LastErrorCode);
        Assert.Equal("Triage delayed: provider unavailable.", delayed.LastErrorMessage);
        var pump = CreatePump(scope.Factory);
        var options = new WorkerOptions { MaxConcurrentJobs = 1, LeaseSeconds = 3, MaxAttempts = 1, RetryDelaySeconds = 1 };
        Assert.Equal(0, await pump.FillAvailableSlotsAsync("worker-provider-recover", options, TestContext.Current.CancellationToken));

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await pump.FillAvailableSlotsAsync("worker-provider-recover", options, TestContext.Current.CancellationToken));
        await WaitForPumpToDrainAsync(pump);

        var recovered = await ReadJobAsync(scope.ConnectionString, ingested.JobId.Value);
        Assert.Equal("Succeeded", recovered.Status);
        Assert.False(scope.Factory.Services.GetRequiredService<IProviderOutageTracker>().IsBackpressured);
        Assert.True(modelClient.RequestCount > 1);
    }
    [DockerAvailableFact]
    public async Task WorkerPumps_RenewLongRunningLeaseBeforeAnotherWorkerCanReclaimTheJob()
    {
        var modelClient = new BlockingFirstRequestModelClient();
        using var scope = await CreateScopeAsync(services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddSingleton(modelClient);
            services.AddScoped<IAiModelClient>(serviceProvider => serviceProvider.GetRequiredService<BlockingFirstRequestModelClient>());
        });
        var ingested = await PostIngestAsync(scope.Client, TesterEnvelope());
        Assert.NotNull(ingested.JobId);

        var options = new WorkerOptions
        {
            MaxConcurrentJobs = 1,
            LeaseSeconds = 3,
            MaxAttempts = 3,
            RetryDelaySeconds = 1
        };
        var firstWorker = CreatePump(scope.Factory);
        var secondWorker = CreatePump(scope.Factory);

        Assert.Equal(1, await firstWorker.FillAvailableSlotsAsync("worker-lease-first", options, TestContext.Current.CancellationToken));
        await modelClient.FirstRequestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

        Assert.Equal(0, await secondWorker.FillAvailableSlotsAsync("worker-lease-second", options, TestContext.Current.CancellationToken));
        modelClient.ReleaseFirstRequest.TrySetResult();
        await WaitForPumpToDrainAsync(firstWorker);

        var job = await ReadJobAsync(scope.ConnectionString, ingested.JobId.Value);
        var reportCount = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        Assert.Equal("Succeeded", job.Status);
        Assert.Equal(1, reportCount);
        // The mock orchestrator script for a single tester ticket is a fixed sequence - delegate
        // to the analysis worker, delegate to the memory worker, then publish_report - so a
        // successful single-attempt run always makes exactly 3 model calls. A range here would
        // hide a change in that turn count instead of catching it.
        Assert.Equal(3, modelClient.RequestCount);
    }

    [DockerAvailableFact]
    public async Task WorkerPump_OwnershipLossCancelsInFlightProviderWorkAndAReclaimedJobPublishesOnce()
    {
        var modelClient = new BlockingFirstRequestModelClient();
        using var scope = await CreateScopeAsync(services =>
        {
            services.RemoveAll<IAiModelClient>();
            services.AddSingleton(modelClient);
            services.AddScoped<IAiModelClient>(serviceProvider => serviceProvider.GetRequiredService<BlockingFirstRequestModelClient>());
        });
        var ingested = await PostIngestAsync(scope.Client, TesterEnvelope());
        Assert.NotNull(ingested.JobId);

        var staleWorker = CreatePump(scope.Factory);
        var recoveringWorker = CreatePump(scope.Factory);
        var options = new WorkerOptions
        {
            MaxConcurrentJobs = 1,
            LeaseSeconds = 3,
            MaxAttempts = 3,
            RetryDelaySeconds = 1
        };

        Assert.Equal(1, await staleWorker.FillAvailableSlotsAsync("worker-stale-owner", options, TestContext.Current.CancellationToken));
        await modelClient.FirstRequestStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(scope.ConnectionString, """
            UPDATE incidentcompass.triage_jobs
            SET attempt = 2,
                locked_by = 'worker-new-owner',
                locked_until_utc = now() + interval '5 minutes'
            WHERE id = @job_id;
            """, ("job_id", ingested.JobId.Value));

        await modelClient.FirstRequestCancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        await WaitForPumpToDrainAsync(staleWorker);

        var staleJob = await ReadJobAsync(scope.ConnectionString, ingested.JobId.Value);
        var reportCountBeforeRecovery = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        Assert.Equal("Processing", staleJob.Status);
        Assert.Equal("worker-new-owner", staleJob.LockedBy);
        Assert.Equal(0, reportCountBeforeRecovery);
        Assert.Equal(1, modelClient.RequestCount);

        await ExecuteAsync(scope.ConnectionString, """
            UPDATE incidentcompass.triage_jobs
            SET locked_until_utc = now() - interval '1 second'
            WHERE id = @job_id;
            """, ("job_id", ingested.JobId.Value));

        Assert.Equal(1, await recoveringWorker.FillAvailableSlotsAsync("worker-recovering-owner", options, TestContext.Current.CancellationToken));
        await WaitForPumpToDrainAsync(recoveringWorker);

        var recoveredJob = await ReadJobAsync(scope.ConnectionString, ingested.JobId.Value);
        var reportCountAfterRecovery = await ScalarAsync<long>(
            scope.ConnectionString,
            "SELECT COUNT(*) FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            ("fault_id", ingested.FaultId));
        Assert.Equal("Succeeded", recoveredJob.Status);
        Assert.Null(recoveredJob.LockedBy);
        Assert.Equal(1, reportCountAfterRecovery);
    }
    private async Task<TestScope> CreateScopeAsync(Action<IServiceCollection>? configureServices = null)
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseSetting("IncidentCompass:ProviderResilience:FailureThreshold", "1");
            builder.UseSetting("IncidentCompass:ProviderResilience:BackpressureSeconds", "1");
            if (configureServices is not null)
            {
                builder.ConfigureTestServices(configureServices);
            }
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        return new TestScope(factory, client, connectionString);
    }

    private static async Task<IngestSignalResponseDto> PostIngestAsync(HttpClient client, object envelope)
    {
        var response = await client.PostAsJsonAsync("/api/v1/incidents", envelope, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestSignalResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        return body;
    }

    private static WorkerJobPump CreatePump(WebApplicationFactory<Program> factory)
    {
        return new WorkerJobPump(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new WorkerJobLeaseRenewer(),
            factory.Services.GetRequiredService<ILogger<WorkerJobPump>>(),
            factory.Services.GetRequiredService<IProviderOutageTracker>());
    }

    private static async Task WaitForPumpToDrainAsync(WorkerJobPump pump)
    {
        await pump.WaitForNextWakeAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await pump.ObserveCompletedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, pump.ActiveJobCount);
    }
    private static TesterEnvelopeDto TesterEnvelope()
    {
        var unique = Guid.NewGuid().ToString("N");
        return new TesterEnvelopeDto(
            "tester",
            "unit4-payments-api-" + unique,
            "prod",
            DateTimeOffset.UtcNow,
            new TesterAttributesDto("ValidationException", "Validation failed for request " + unique, "/validate"));
    }

    private static async Task<JobRow> ReadJobAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, locked_by, last_error_code, last_error_message FROM incidentcompass.triage_jobs WHERE id = @job_id;",
            connection);
        command.Parameters.AddWithValue("job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new JobRow(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task<ReportRow> ReadReportAsync(string connectionString, Guid faultId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, classification, confidence, config_hash FROM incidentcompass.triage_reports WHERE fault_id = @fault_id;",
            connection);
        command.Parameters.AddWithValue("fault_id", faultId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ReportRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }

    private static async Task<WorkerArtifactRow> ReadWorkerArtifactAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT attempt, domain_ref, redacted_payload->>'candidateClassification'
            FROM incidentcompass.triage_artifacts
            WHERE job_id = @job_id AND kind = 'WorkerOutput';
            """,
            connection);
        command.Parameters.AddWithValue("job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new WorkerArtifactRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<IReadOnlyList<LedgerRow>> ReadLedgerRowsAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT id, event_type, role, tool_name, config_hash FROM incidentcompass.triage_ledger WHERE job_id = @job_id ORDER BY id;",
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
                reader.GetString(4)));
        }

        return rows;
    }

    private static void AssertLedger(
        LedgerRow row,
        int index,
        string eventType,
        string? role,
        string? toolName,
        string configHash)
    {
        Assert.Equal(eventType, row.EventType);
        Assert.Equal(role, row.Role);
        Assert.Equal(toolName, row.ToolName);
        Assert.Equal(configHash, row.ConfigHash);
        Assert.True(row.Id > index);
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
    private sealed class CrashAfterDelegationModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            if (request.Tools is { Count: > 0 })
            {
                return Task.FromResult(new AiModelResponse(
                    "Propose analysis delegation before the injected crash.",
                    request.Model,
                    "test-crash",
                    Usage: null,
                    request.CorrelationId,
                    [CreateToolCall()]));
            }

            throw new InvalidOperationException("Injected worker crash after Delegated ledger event.");
        }

        private static AiToolCall CreateToolCall()
        {
            using var arguments = JsonDocument.Parse(
                """{"role":"analysis","task":"Fail after the Delegated ledger event is committed."}""");
            return new AiToolCall("crash-delegate-analysis-1", "delegate", "v1", arguments.RootElement.Clone());
        }
    }

    private sealed class ProviderOutageThenSuccessModelClient : IAiModelClient
    {
        private readonly MockAiModelClient inner = new();
        private int requestCount;

        public int RequestCount => Volatile.Read(ref requestCount);

        public Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                throw new AiModelException(
                    "test-provider",
                    "Service unavailable.",
                    failureKind: ProviderFailureKind.Unavailable);
            }

            return inner.CompleteAsync(request, cancellationToken);
        }
    }

    private sealed class BlockingFirstRequestModelClient : IAiModelClient
    {
        private readonly MockAiModelClient inner = new();
        private int requestCount;
        private int firstRequestStarted;

        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstRequestCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount => Volatile.Read(ref requestCount);

        public async Task<AiModelResponse> CompleteAsync(AiModelRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            if (Interlocked.CompareExchange(ref firstRequestStarted, 1, 0) == 0)
            {
                FirstRequestStarted.TrySetResult();
                try
                {
                    await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    FirstRequestCancelled.TrySetResult();
                    throw;
                }
            }

            return await inner.CompleteAsync(request, cancellationToken);
        }
    }
    private sealed record TestScope(WebApplicationFactory<Program> Factory, HttpClient Client, string ConnectionString) : IDisposable
    {
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
        }
    }

    private sealed record TesterAttributesDto(string ErrorType, string ErrorMessage, string HttpRoute);

    private sealed record TesterEnvelopeDto(
        string SourceKind,
        string ServiceName,
        string Environment,
        DateTimeOffset ObservedAtUtc,
        TesterAttributesDto Attributes);

    private sealed record IngestSignalResponseDto(
        Guid SignalId,
        Guid FaultId,
        bool IsNewFault,
        bool IsNewJob,
        bool IsSuppressed,
        Guid? JobId,
        string? ConfigHash);

    private sealed record JobRow(string Status, string? LockedBy, string? LastErrorCode, string? LastErrorMessage);

    private sealed record ReportRow(string Status, string Classification, string Confidence, string ConfigHash);

    private sealed record WorkerArtifactRow(int Attempt, string DomainRef, string CandidateClassification);

    private sealed record LedgerRow(long Id, string EventType, string? Role, string? ToolName, string ConfigHash);
}
