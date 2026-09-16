using System.Net;
using System.Net.Http.Json;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;
using IncidentCompass.Infrastructure.Memory;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// A triage job whose <c>memory_search</c> reaches the real local embedding adapter while the local
/// model is closed: installed under another id, or not usable at all. The job runs through the real
/// runner, investigation loop, tool path and PostgreSQL job store; only the install state is set
/// directly, because how a model is fetched or verified is covered by the store tests. The refusal is
/// an operator-fixable configuration state, so the job must store the state's code, retry only
/// inside its configured attempt budget and dead-letter at the end, and never feed the outage pause.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class MemorySearchClosedLocalModelTests(PostgresRepositoryFixture postgres)
{
    private const int MaxAttempts = 2;

    [DockerAvailableFact]
    public async Task MemorySearch_WithAnInstalledModelOfAnotherId_DeadLettersAfterTheAttemptBudgetWithoutAnOutagePause() =>
        await AssertBoundedWithoutOutagePauseAsync(InstalledUnderAnotherId(), MemoryCorpusErrorCodes.EmbeddingModelMismatch);

    [DockerAvailableFact]
    public async Task MemorySearch_WithNoUsableModel_DeadLettersAfterTheAttemptBudgetWithoutAnOutagePause() =>
        await AssertBoundedWithoutOutagePauseAsync(FailedInstall(), MemoryCorpusErrorCodes.EmbeddingModelUnavailable);

    private async Task AssertBoundedWithoutOutagePauseAsync(LocalOnnxModelInstallState installState, string expectedCode)
    {
        var tracker = new RecordingProviderOutageTracker();
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await PostgresTriageJobTestIsolation.CompleteClaimableJobsAsync(connectionString);
        using var factory = CreateFactory(connectionString, installState, tracker);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var jobId = await PostKnownTimeoutAsync(client);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
            var claimed = await runner.ClaimNextAsync("worker-closed-model", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            Assert.NotNull(claimed);
            Assert.Equal(jobId, claimed.Id);
            Assert.Equal(attempt, claimed.Attempt);

            await runner.ProcessClaimedAsync(
                claimed,
                "worker-closed-model",
                new TriageJobProcessingSettings(MaxAttempts, RetryDelay: TimeSpan.Zero),
                TestContext.Current.CancellationToken);

            var job = await ReadJobAsync(connectionString, jobId);
            Assert.Equal(expectedCode, job.LastErrorCode);
            Assert.Equal(attempt, job.Attempt);
            Assert.Equal(attempt < MaxAttempts ? "RetryPending" : "DeadLettered", job.Status);
            Assert.Equal(attempt, await CountMemorySearchProposalsAsync(connectionString, jobId));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
            var next = await runner.ClaimNextAsync("worker-closed-model", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
            Assert.Null(next);
        }

        Assert.Equal(0, tracker.FailureCount);
        Assert.False(tracker.IsBackpressured);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string connectionString,
        LocalOnnxModelInstallState installState,
        RecordingProviderOutageTracker tracker) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseExplicitMockProviders();
            builder.UseWorkerModelHost();
            builder.UseSetting("IncidentCompass:ProviderResilience:FailureThreshold", "1");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IProviderOutageTracker>();
                services.AddSingleton<IProviderOutageTracker>(tracker);
                services.RemoveAll<IEmbeddingClient>();
                services.AddScoped<IEmbeddingClient>(provider => new LocalOnnxEmbeddingClient(
                    new LocalOnnxInstalledModelReader(
                        Options.Create(new EmbeddingOptions { Provider = "LocalOnnx" }),
                        Options.Create(new LocalOnnxEmbeddingOptions { ModelDirectory = Path.GetTempPath() }),
                        installState,
                        provider.GetRequiredService<LocalOnnxModelStore>()),
                    provider.GetRequiredService<LocalOnnxModelRuntime>(),
                    provider.GetRequiredService<ITriageConfigurationRepository>()));
            });
        });

    private static LocalOnnxModelInstallState InstalledUnderAnotherId()
    {
        var options = new LocalOnnxEmbeddingOptions
        {
            ModelDirectory = Path.GetTempPath(),
            ModelId = "incidentcompass-test/not-the-route-model",
            ModelFileSha256 = new string('a', 64)
        };
        var state = new LocalOnnxModelInstallState();
        state.RecordInstalled(new LocalOnnxInstalledModel(LocalOnnxModelStore.CreateManifest(options), "unused.onnx", "unused.model"));
        return state;
    }

    private static LocalOnnxModelInstallState FailedInstall()
    {
        var state = new LocalOnnxModelInstallState();
        state.RecordFailed(LocalOnnxModelErrorCodes.FetchFailed, "The onnx file could not be downloaded in this test.");
        return state;
    }

    private static async Task<Guid> PostKnownTimeoutAsync(HttpClient client)
    {
        var envelope = new
        {
            SourceKind = "tester",
            ServiceName = "payments-api-" + Guid.NewGuid().ToString("N"),
            Environment = "prod",
            ObservedAtUtc = DateTimeOffset.UtcNow,
            Attributes = new
            {
                ErrorType = "TimeoutException",
                ErrorMessage = "Checkout timed out while calling inventory",
                HttpRoute = "/checkout"
            }
        };
        var response = await client.PostAsJsonAsync("/api/v1/incidents", envelope, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<IngestSignalResponseDto>(TestContext.Current.CancellationToken);
        Assert.NotNull(body);
        Assert.NotNull(body.JobId);
        return body.JobId.Value;
    }

    private static async Task<JobRow> ReadJobAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT status, attempt, last_error_code FROM incidentcompass.triage_jobs WHERE id = @job_id;",
            connection);
        command.Parameters.AddWithValue("job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new JobRow(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task<long> CountMemorySearchProposalsAsync(string connectionString, Guid jobId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT COUNT(*) FROM incidentcompass.triage_ledger
            WHERE job_id = @job_id AND event_type = 'ToolProposed' AND tool_name = 'memory_search';
            """,
            connection);
        command.Parameters.AddWithValue("job_id", jobId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record JobRow(string Status, int Attempt, string? LastErrorCode);

    private sealed record IngestSignalResponseDto(Guid FaultId, Guid? JobId);

    private sealed class RecordingProviderOutageTracker : IProviderOutageTracker
    {
        private int failureCount;

        public int FailureCount => Volatile.Read(ref failureCount);

        public bool IsBackpressured => FailureCount > 0;

        public TimeSpan RetryDelay => TimeSpan.FromMinutes(1);

        public void RecordProviderFailure() => Interlocked.Increment(ref failureCount);

        public void RecordProviderSuccess()
        {
        }
    }
}
