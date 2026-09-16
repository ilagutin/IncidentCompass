using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Memory;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

public sealed record MemoryRetrievalBenchmarkCorpus(
    int SchemaVersion,
    string CorpusVersion,
    Guid GenerationId,
    string TenantId,
    string SeedOwner,
    string EmbeddingModel,
    int EmbeddingDimensions,
    int TopK,
    double MinScore,
    IReadOnlyDictionary<string, string> CurrentReleases,
    IReadOnlyList<MemoryRetrievalBenchmarkItem> Items,
    IReadOnlyList<MemoryRetrievalBenchmarkQuery> Queries)
{
    public static MemoryRetrievalBenchmarkCorpus Load(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var path = Path.Combine(
            repoRoot,
            "tests",
            "IncidentCompass.IntegrationTests",
            "Fixtures",
            "MemoryRetrieval",
            "corpus-v1.json");
        var corpus = JsonSerializer.Deserialize<MemoryRetrievalBenchmarkCorpus>(
            File.ReadAllText(path),
            SerializerOptions) ?? throw new InvalidOperationException("Memory retrieval corpus is empty.");
        corpus.Validate();
        return corpus;
    }

    internal Task SeedAsync(
        string connectionString,
        IEmbeddingClient embeddingClient,
        IMemoryRepository repository,
        CancellationToken cancellationToken) =>
        SeedCoreAsync(
            connectionString,
            embeddingClient,
            repository,
            EmbeddingModel,
            new MemoryCorpusIdentity("memory-embed", "benchmark", "mock", EmbeddingModel, EmbeddingDimensions),
            cancellationToken);

    /// <summary>
    /// Seeds the corpus with vectors from a real embedding identity, such as the local model. Every
    /// passage vector must report exactly <paramref name="identity" />'s provider, model and width, so
    /// a corpus is never published under an identity its vectors do not have. The mock-only
    /// <see cref="SeedAsync" /> is what the versioned baseline uses and is unchanged.
    /// </summary>
    internal Task SeedWithEmbeddingIdentityAsync(
        string connectionString,
        IEmbeddingClient embeddingClient,
        IMemoryRepository repository,
        string requestModel,
        MemoryCorpusIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestModel);
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.EmbeddingProvider == "mock")
        {
            throw new ArgumentException("Mock corpora are seeded through SeedAsync.", nameof(identity));
        }

        return SeedCoreAsync(connectionString, embeddingClient, repository, requestModel, identity, cancellationToken);
    }

    private async Task SeedCoreAsync(
        string connectionString,
        IEmbeddingClient embeddingClient,
        IMemoryRepository repository,
        string requestModel,
        MemoryCorpusIdentity identity,
        CancellationToken cancellationToken)
    {
        await ClearAsync(connectionString, cancellationToken);
        var entries = new List<MemorySeedEntry>(Items.Count);
        foreach (var fixtureItem in Items)
        {
            var chunks = new List<MemorySeedChunk>(fixtureItem.Chunks.Count);
            foreach (var fixtureChunk in fixtureItem.Chunks.OrderBy(static chunk => chunk.Position))
            {
                var embedding = await embeddingClient.CreateEmbeddingAsync(
                    new EmbeddingRequest(fixtureChunk.Text, requestModel, "memory-benchmark-seed", EmbeddingInputKind.Passage),
                    cancellationToken);
                if (identity.EmbeddingProvider == "mock")
                {
                    if (embedding.Provider != "mock" || embedding.Vector.Count != EmbeddingDimensions)
                    {
                        throw new InvalidOperationException("Benchmark seeding requires the configured mock embedding route.");
                    }
                }
                else if (embedding.Provider != identity.EmbeddingProvider ||
                         embedding.Model != identity.EmbeddingModel ||
                         embedding.Vector.Count != identity.EmbeddingDimensions)
                {
                    throw new InvalidOperationException(
                        "Benchmark seeding received a vector from another embedding identity than the corpus records.");
                }

                chunks.Add(new MemorySeedChunk(
                    fixtureChunk.Id,
                    fixtureChunk.Position,
                    fixtureChunk.Text,
                    Hash(fixtureChunk.Text),
                    embedding.Provider,
                    embedding.Model,
                    embedding.Vector.Count,
                    embedding.Vector));
            }

            entries.Add(new MemorySeedEntry(
                new MemorySeedItem(
                    fixtureItem.Id,
                    TenantId,
                    fixtureItem.Kind,
                    fixtureItem.Source,
                    fixtureItem.Title,
                    fixtureItem.Content,
                    Hash(fixtureItem.Content),
                    1,
                    fixtureItem.Tags,
                    fixtureItem.ServiceName,
                    fixtureItem.Component,
                    fixtureItem.ReleaseName),
                chunks));
        }

        await repository.ReconcileSeedCorpusAsync(
            new MemorySeedCorpus(
                TenantId,
                SeedOwner,
                GenerationId,
                identity,
                new HashSet<string>(StringComparer.Ordinal) { "benchmark" },
                entries),
            cancellationToken);
        await DeactivateFixtureItemsAsync(connectionString, cancellationToken);
    }

    private void Validate()
    {
        if (SchemaVersion != 1 || CorpusVersion != "memory-retrieval-corpus-v1" || TopK != 5)
        {
            throw new InvalidOperationException("Unsupported memory retrieval corpus contract.");
        }

        if (Items.Count == 0 || Queries.Count == 0 || EmbeddingDimensions <= 0)
        {
            throw new InvalidOperationException("Memory retrieval corpus must contain items, queries and embedding dimensions.");
        }

        RequireUnique(Items.Select(static item => item.Id), "item ids");
        RequireUnique(Items.SelectMany(static item => item.Chunks).Select(static chunk => chunk.Id), "chunk ids");
        RequireUnique(Queries.Select(static query => query.Id), "query ids");
        var itemIds = Items.Select(static item => item.Id).ToHashSet();
        var chunkIds = Items.SelectMany(static item => item.Chunks).Select(static chunk => chunk.Id).ToHashSet();
        if (Queries.Any(query => !query.RelevantItemIds.All(itemIds.Contains) ||
                                 !query.RelevantChunkIds.All(chunkIds.Contains)))
        {
            throw new InvalidOperationException("Every relevance label must reference a fixture item and chunk.");
        }

        if (!Queries.Any(static query => query.RelevantChunkIds.Count > 0) ||
            !Queries.Any(static query => query.RelevantChunkIds.Count == 0))
        {
            throw new InvalidOperationException("Corpus must contain at least one positive and one true no-match query.");
        }

        if (Items.SelectMany(static item => item.Chunks).GroupBy(static chunk => chunk.Position)
            .Any(static group => group.Key < 0))
        {
            throw new InvalidOperationException("Chunk positions must be non-negative.");
        }
    }

    private async Task DeactivateFixtureItemsAsync(string connectionString, CancellationToken cancellationToken)
    {
        var inactiveIds = Items.Where(static item => !item.IsActive).Select(static item => item.Id).ToArray();
        if (inactiveIds.Length == 0)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE incidentcompass.memory_items SET is_active = false WHERE id = ANY(@ids);",
            connection);
        command.Parameters.AddWithValue("ids", inactiveIds);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ClearAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM incidentcompass.memory_items;", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void RequireUnique<T>(IEnumerable<T> values, string label) where T : notnull
    {
        var materialized = values.ToArray();
        if (materialized.Distinct().Count() != materialized.Length)
        {
            throw new InvalidOperationException("Memory retrieval corpus contains duplicate " + label + ".");
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed record MemoryRetrievalBenchmarkItem(
    Guid Id,
    string Kind,
    string Source,
    string Title,
    string Content,
    IReadOnlyList<string> Tags,
    string? ServiceName,
    string? Component,
    string? ReleaseName,
    bool IsActive,
    IReadOnlyList<MemoryRetrievalBenchmarkChunk> Chunks);

public sealed record MemoryRetrievalBenchmarkChunk(Guid Id, int Position, string Text);

public sealed record MemoryRetrievalBenchmarkQuery(
    string Id,
    string Text,
    string ServiceName,
    IReadOnlyList<Guid> RelevantItemIds,
    IReadOnlyList<Guid> RelevantChunkIds);
