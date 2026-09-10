using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Intake;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Host composition and database readbacks shared by the memory corpus rebuild tests. Every host it
/// builds is a real Infrastructure composition; only the embedding adapter and the memory embedding
/// route are substituted, because those are exactly the two things a route change moves.
/// </summary>
internal static class MemoryCorpusTestSupport
{
    public const string TenantId = "local";

    public static IHost CreateHost(
        string connectionString,
        string sourceDirectory,
        string owner,
        IEmbeddingClient embeddingClient,
        string embeddingModel,
        string providerId = "local-oai",
        bool seedingEnabled = true) =>
        new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:IncidentCompass"] = connectionString,
                    ["IncidentCompass:ConfigSource:Path"] =
                        Path.Combine(RepositoryRootLocator.Find(), "config", "incidentcompass.config.json"),
                    ["IncidentCompass:Memory:Seed:Enabled"] = seedingEnabled ? "true" : "false",
                    ["IncidentCompass:Memory:Seed:TenantId"] = TenantId,
                    ["IncidentCompass:Memory:Seed:Owner"] = owner,
                    ["IncidentCompass:Memory:Seed:RuntimeResyncEnabled"] = "false",
                    ["IncidentCompass:Memory:Seed:RuntimeResyncIntervalSeconds"] = "60",
                    ["IncidentCompass:Memory:Seed:SourceDirectory"] = sourceDirectory
                }))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddSingleton<IEmbeddingClient>(embeddingClient);
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);

                // Registered last so it wins resolution: the file-backed repository still supplies
                // the whole configuration, and only the memory embedding route is rewritten.
                services.AddScoped<ITriageConfigurationRepository>(serviceProvider =>
                    new MemoryRouteOverridingConfigurationRepository(
                        serviceProvider.GetRequiredService<FileTriageConfigurationRepository>(),
                        embeddingModel,
                        providerId));
            })
            .Build();

    public static async Task<string> CreateSeedDirectoryAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "incidentcompass-memory-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "runbooks"));
        Directory.CreateDirectory(Path.Combine(directory, "incidents"));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "runbooks", "checkout-timeout.md"),
            "---\nkind: Runbook\nservice: checkout-api\n---\n\n# Checkout Timeout\n\nUpstream payment latency.",
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "incidents", "provider-unavailable.md"),
            "---\nkind: KnownIncident\nservice: provider-client\n---\n\n# Provider Unavailable\n\nDependency outage.",
            cancellationToken);
        return directory;
    }

    public static async Task ClearMemoryAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            DELETE FROM incidentcompass.memory_items;
            DELETE FROM incidentcompass.memory_corpus_generations;
            """,
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task<MemoryCorpusRow> ReadCorpusAsync(
        string connectionString,
        string owner,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM incidentcompass.memory_items
                  WHERE tenant_id = @tenant_id AND seed_owner = @owner AND is_active = true),
                (SELECT count(*) FROM incidentcompass.memory_chunks chunk
                   JOIN incidentcompass.memory_items item ON item.id = chunk.memory_item_id
                  WHERE item.tenant_id = @tenant_id AND item.seed_owner = @owner AND item.is_active = true),
                (SELECT count(DISTINCT item.seed_generation) FROM incidentcompass.memory_items item
                  WHERE item.tenant_id = @tenant_id AND item.seed_owner = @owner AND item.is_active = true),
                (SELECT count(*) FROM incidentcompass.memory_corpus_generations
                  WHERE tenant_id = @tenant_id AND seed_owner = @owner AND is_current),
                (SELECT string_agg(DISTINCT chunk.embedding_model || ':' || chunk.embedding_dimensions, ',')
                   FROM incidentcompass.memory_chunks chunk
                   JOIN incidentcompass.memory_items item ON item.id = chunk.memory_item_id
                  WHERE item.tenant_id = @tenant_id AND item.seed_owner = @owner AND item.is_active = true),
                (SELECT generation FROM incidentcompass.memory_corpus_generations
                  WHERE tenant_id = @tenant_id AND seed_owner = @owner AND is_current),
                (SELECT provider_id FROM incidentcompass.memory_corpus_generations
                  WHERE tenant_id = @tenant_id AND seed_owner = @owner AND is_current);
            """, connection);
        command.Parameters.AddWithValue("tenant_id", TenantId);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new MemoryCorpusRow(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    /// <summary>
    /// Deterministic vectors whose width is decided by the requested model, so a test can change a
    /// model the way an operator does and get a genuinely different vector space back.
    /// </summary>
    internal sealed class ModelSizedEmbeddingClient(
        IReadOnlyDictionary<string, int> dimensionsByModel,
        string adapterName = "test") : IEmbeddingClient
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref callCount);
            if (!dimensionsByModel.TryGetValue(request.Model, out var dimensions))
            {
                throw new InvalidOperationException(
                    "The test embedding client has no width configured for model '" + request.Model + "'.");
            }

            var vector = new float[dimensions];
            vector[Math.Abs(request.Input.Length) % dimensions] = 1f;
            return Task.FromResult(new EmbeddingResponse(
                vector, request.Model, adapterName, 1, request.CorrelationId));
        }
    }

    /// <summary>Fails once the given number of embeddings have succeeded.</summary>
    internal sealed class FailingEmbeddingClient(int successfulCalls, int dimensions) : IEmbeddingClient
    {
        private int callCount;

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref callCount) > successfulCalls)
            {
                throw new InvalidOperationException("Simulated embedding failure.");
            }

            var vector = new float[dimensions];
            vector[0] = 1f;
            return Task.FromResult(new EmbeddingResponse(vector, request.Model, "test", 1, request.CorrelationId));
        }
    }

    /// <summary>Cancels the supplied source once the given number of embeddings have succeeded.</summary>
    internal sealed class CancellingEmbeddingClient(
        int successfulCalls,
        int dimensions,
        CancellationTokenSource cancellation) : IEmbeddingClient
    {
        private int callCount;

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref callCount) > successfulCalls)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            var vector = new float[dimensions];
            vector[0] = 1f;
            return Task.FromResult(new EmbeddingResponse(vector, request.Model, "test", 1, request.CorrelationId));
        }
    }

    internal sealed record MemoryCorpusRow(
        long ActiveItems,
        long ActiveChunks,
        long DistinctItemGenerations,
        long CurrentGenerationRows,
        string? EmbeddingIdentities,
        Guid? CurrentGeneration,
        string? CurrentProviderId)
    {
        public string Identities => EmbeddingIdentities ?? string.Empty;
    }

    private sealed class MemoryRouteOverridingConfigurationRepository(
        ITriageConfigurationRepository inner,
        string embeddingModel,
        string providerId) : ITriageConfigurationRepository
    {
        public async Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
            Override(await inner.GetCurrentAsync(cancellationToken));

        public async Task<TriageConfiguration> GetByHashAsync(
            string configHash,
            CancellationToken cancellationToken) =>
            Override(await inner.GetByHashAsync(configHash, cancellationToken));

        private TriageConfiguration Override(TriageConfiguration configuration)
        {
            var routeId = configuration.Tools["memory_search"].EmbeddingRouteId!;
            var routes = new Dictionary<string, TriageRouteSettings>(configuration.Routes, StringComparer.Ordinal)
            {
                [routeId] = configuration.Routes[routeId] with
                {
                    Model = embeddingModel,
                    ProviderId = providerId
                }
            };
            return configuration with { Routes = routes };
        }
    }
}
