using System.Net;
using System.Net.Http.Json;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Memory;
using IncidentCompass.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class MemorySyncHealthEndpointTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task MemorySyncHealth_ReturnsPersistedWorkerSuccessStatus()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearStatusAsync(connectionString);
        var owner = "health-" + Guid.NewGuid().ToString("N");
        using var worker = CreateWorkerHost(connectionString, CreateSourceDirectory(), owner);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        var workerStatus = worker.Services.GetRequiredService<IMemorySeedSyncStatus>().Snapshot;
        var response = await GetApiStatusAsync(connectionString, owner);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<MemorySyncHealthResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.True(status.Enabled);
        Assert.True(status.RuntimeResyncEnabled);
        Assert.NotNull(workerStatus.LastAttemptAtUtc);
        Assert.NotNull(workerStatus.LastSuccessAtUtc);
        Assert.NotNull(status.LastAttemptAtUtc);
        Assert.NotNull(status.LastSuccessAtUtc);
        Assert.Equal(workerStatus.ActiveGeneration, status.ActiveGeneration);
        Assert.Null(status.LastErrorCode);
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [DockerAvailableFact]
    public async Task MemorySyncHealth_ReturnsPersistedWorkerFailureStatus()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearStatusAsync(connectionString);
        var owner = "health-failure-" + Guid.NewGuid().ToString("N");
        using var worker = CreateWorkerHost(
            connectionString,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            owner);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => worker.StartAsync(TestContext.Current.CancellationToken));
        var response = await GetApiStatusAsync(connectionString, owner);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<MemorySyncHealthResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(status);
        Assert.True(status.Enabled);
        Assert.Equal("memory_sync_failed", status.LastErrorCode);
    }

    private async Task<string> CreateSchemaAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        return connectionString;
    }

    private static IHost CreateWorkerHost(string connectionString, string sourceDirectory, string owner = "default") =>
        new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:IncidentCompass"] = connectionString,
                    ["IncidentCompass:ConfigSource:Path"] = Path.Combine(RepositoryRootLocator.Find(), "config", "incidentcompass.config.json"),
                    ["IncidentCompass:Memory:Seed:Enabled"] = "true",
                    ["IncidentCompass:Memory:Seed:TenantId"] = "local",
                    ["IncidentCompass:Memory:Seed:Owner"] = owner,
                    ["IncidentCompass:Memory:Seed:RuntimeResyncEnabled"] = "true",
                    ["IncidentCompass:Memory:Seed:RuntimeResyncIntervalSeconds"] = "60",
                    ["IncidentCompass:Memory:Seed:SourceDirectory"] = sourceDirectory
                });
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddSingleton<IEmbeddingClient, DeterministicEmbeddingClient>();
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
                services.AddEmbeddingHost(context.Configuration);
            })
            .Build();

    private static async Task<HttpResponseMessage> GetApiStatusAsync(string connectionString, string owner = "default")
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseSetting("IncidentCompass:Memory:Seed:Owner", owner);
            builder.UseExplicitMockProviders();
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        return await client.GetAsync("/api/v1/health/memory-sync", TestContext.Current.CancellationToken);
    }

    private static string CreateSourceDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "incidentcompass-memory-health-" + Guid.NewGuid().ToString("N"));
        var runbooks = Path.Combine(directory, "runbooks");
        Directory.CreateDirectory(runbooks);
        File.WriteAllText(Path.Combine(runbooks, "health.md"), "# Memory Health\nShared status test.");
        return directory;
    }

    private static async Task ClearStatusAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM incidentcompass.memory_seed_sync_status;", connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private sealed class DeterministicEmbeddingClient : IEmbeddingClient
    {
        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EmbeddingResponse([1f, 0f], request.Model, "test", 1, request.CorrelationId));
    }

    private sealed record MemorySyncHealthResponse(
        bool Enabled,
        bool RuntimeResyncEnabled,
        DateTimeOffset? LastAttemptAtUtc,
        DateTimeOffset? LastSuccessAtUtc,
        Guid? ActiveGeneration,
        string? LastErrorCode);
}
