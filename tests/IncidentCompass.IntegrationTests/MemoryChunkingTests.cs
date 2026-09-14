using System.Net;
using System.Net.Http.Json;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Memory;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class MemoryChunkingTests(PostgresRepositoryFixture postgres)
{
    private static readonly string[] SectionNames = ["Symptoms", "Mitigation", "Verification"];

    [DockerAvailableFact]
    public async Task Seed_LongDocumentPublishesEverySectionAndPolicyChangeRequiresARebuild()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await MemoryCorpusTestSupport.ClearMemoryAsync(connectionString, cancellation);
        var directory = await MemoryCorpusTestSupport.CreateSeedDirectoryAsync(cancellation);
        var owner = "chunk-policy-" + Guid.NewGuid().ToString("N");
        var content = "---\nkind: Runbook\nservice: checkout-api\n---\n\n# Checkout\n\n" +
            string.Join('\n', SectionNames.Select(section =>
                "## " + section + "\n" + string.Join('\n', Enumerable.Range(0, 80).Select(index =>
                    $"{section} checkpoint {index:00}: inspect the checkout service."))));
        await File.WriteAllTextAsync(Path.Combine(directory, "runbooks", "checkout-timeout.md"), content, cancellation);
        var embedding = new MemoryCorpusTestSupport.ModelSizedEmbeddingClient(new Dictionary<string, int> { ["model"] = 4 });

        using var original = MemoryCorpusTestSupport.CreateHost(connectionString, directory, owner, embedding, "model");
        await original.StartAsync(cancellation);
        await original.StopAsync(cancellation);
        using var originalScope = original.Services.CreateScope();
        var repository = originalScope.ServiceProvider.GetRequiredService<IMemoryRepository>();
        var inventory = await repository.GetCorpusInventoryAsync("local", owner, cancellation);
        Assert.Equal("v1;max=448;overlap=48;min=32;estimate", inventory.Current!.ChunkPolicy);
        Assert.True(inventory.ActiveChunkCount > 4);
        var chunks = await ReadChunksAsync(connectionString, owner, cancellation);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(static chunk => chunk.Position));
        Assert.Equal(["Checkout > Symptoms", "Checkout > Mitigation", "Checkout > Verification"],
            chunks.Select(static chunk => chunk.Path).Distinct());
        var counter = new CharacterEstimateChunkTokenCounter();
        Assert.All(chunks, chunk => Assert.InRange(counter.CountTokens(chunk.Text), 1, 448));
        var joined = string.Join('\n', chunks.Select(static chunk => chunk.Text));
        foreach (var section in SectionNames)
        {
            foreach (var index in Enumerable.Range(0, 80))
            {
                Assert.Contains($"{section} checkpoint {index:00}:", joined);
            }
        }

        var previousCalls = embedding.CallCount;
        using var changed = MemoryCorpusTestSupport.CreateHost(connectionString, directory, owner, embedding, "model",
            settings: new Dictionary<string, string?> { ["IncidentCompass:Memory:Seed:Chunking:MaxTokens"] = "300" });
        await changed.StartAsync(cancellation);
        using var changedScope = changed.Services.CreateScope();
        var status = await changedScope.ServiceProvider.GetRequiredService<IMemoryCorpusStatusReader>().GetAsync(cancellation);
        var sync = await changedScope.ServiceProvider.GetRequiredService<IMemorySeedSyncStatusReader>().GetAsync(cancellation);
        Assert.Equal(nameof(MemoryCorpusState.ChunkPolicyChanged), status.State);
        Assert.True(status.RebuildRequired);
        Assert.Equal("memory_chunk_policy_changed", sync.LastErrorCode);
        Assert.Equal(inventory.Current.Generation, status.ActiveGeneration);
        Assert.Equal(previousCalls, embedding.CallCount);
        var configuration = await originalScope.ServiceProvider.GetRequiredService<ITriageConfigurationRepository>()
            .GetCurrentAsync(cancellation);
        await AssertApiStateAsync(connectionString, owner, configuration, nameof(MemoryCorpusState.ChunkPolicyChanged));
        Assert.Equal(1, await MemoryCorpusCommand.RunIfRequestedAsync(["memory", "status"], changed.Services, cancellation));
        await changed.StopAsync(cancellation);

        Assert.Equal(0, await MemoryCorpusCommand.RunIfRequestedAsync(["memory", "rebuild"], changed.Services, cancellation));
        var rebuilt = await repository.GetCorpusInventoryAsync("local", owner, cancellation);
        Assert.NotEqual(inventory.Current.Generation, rebuilt.Current!.Generation);
        Assert.Equal("v1;max=300;overlap=48;min=32;estimate", rebuilt.Current.ChunkPolicy);
        Assert.True(rebuilt.ActiveChunkCount > inventory.ActiveChunkCount);
        Assert.All(await ReadChunksAsync(connectionString, owner, cancellation),
            chunk => Assert.InRange(counter.CountTokens(chunk.Text), 1, 300));
        var current = await changedScope.ServiceProvider.GetRequiredService<IMemoryCorpusStatusReader>().GetAsync(cancellation);
        Assert.Equal(nameof(MemoryCorpusState.Current), current.State);
        Assert.False(current.RebuildRequired);
        await AssertApiStateAsync(connectionString, owner, configuration, nameof(MemoryCorpusState.Current));
        var resolvedSync = await changedScope.ServiceProvider.GetRequiredService<IMemorySeedSyncStatusReader>().GetAsync(cancellation);
        Assert.Null(resolvedSync.LastErrorCode);
        Assert.Equal(rebuilt.Current.Generation, resolvedSync.ActiveGeneration);

        // A background pass may finish persisting its previous-generation observation after the
        // rebuild. The status reader must not revive that obsolete chunk-policy block.
        var statusWriter = changedScope.ServiceProvider.GetRequiredService<IMemorySeedSyncStatusWriter>();
        await statusWriter.SaveAsync(sync, cancellation);
        Assert.Null((await changedScope.ServiceProvider.GetRequiredService<IMemorySeedSyncStatusReader>()
            .GetAsync(cancellation)).LastErrorCode);
        await AssertApiStateAsync(connectionString, owner, configuration, nameof(MemoryCorpusState.Current));
        await statusWriter.SaveAsync(sync with { LastErrorCode = MemoryCorpusErrorCodes.EmbeddingRouteChanged }, cancellation);
        Assert.Equal(MemoryCorpusErrorCodes.EmbeddingRouteChanged,
            (await changedScope.ServiceProvider.GetRequiredService<IMemorySeedSyncStatusReader>().GetAsync(cancellation)).LastErrorCode);
    }

    [DockerAvailableFact]
    public async Task Seed_LegacyUnrecordedPolicyRemainsHonestUntilRebuild()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        await MemoryCorpusTestSupport.ClearMemoryAsync(connectionString, cancellation);
        var directory = await MemoryCorpusTestSupport.CreateSeedDirectoryAsync(cancellation);
        var owner = "legacy-policy-" + Guid.NewGuid().ToString("N");
        var embedding = new MemoryCorpusTestSupport.ModelSizedEmbeddingClient(new Dictionary<string, int> { ["model"] = 4 });
        using var host = MemoryCorpusTestSupport.CreateHost(connectionString, directory, owner, embedding, "model");
        await host.StartAsync(cancellation);
        await host.StopAsync(cancellation);
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellation);
            await using var command = new NpgsqlCommand("""
                UPDATE incidentcompass.memory_corpus_generations SET chunk_policy = NULL WHERE seed_owner = @owner;
                """, connection);
            command.Parameters.AddWithValue("owner", owner);
            await command.ExecuteNonQueryAsync(cancellation);
        }

        using var scope = host.Services.CreateScope();
        var pass = await scope.ServiceProvider.GetRequiredService<MemorySeedSynchronizer>()
            .SynchronizeAsync(MemorySeedSyncMode.Incremental, cancellation);
        Assert.True(pass.Published);
        Assert.Equal(2, embedding.CallCount);
        var repository = scope.ServiceProvider.GetRequiredService<IMemoryRepository>();
        Assert.Null((await repository.GetCorpusInventoryAsync("local", owner, cancellation)).Current!.ChunkPolicy);
        Assert.Equal(0, await MemoryCorpusCommand.RunIfRequestedAsync(["memory", "rebuild"], host.Services, cancellation));
        Assert.NotNull((await repository.GetCorpusInventoryAsync("local", owner, cancellation)).Current!.ChunkPolicy);
    }

    private static async Task<List<(int Position, string Path, string Text)>> ReadChunksAsync(
        string connectionString, string owner, CancellationToken cancellation)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellation);
        await using var command = new NpgsqlCommand("""
            SELECT chunk.chunk_position, chunk.heading_path, chunk.text
            FROM incidentcompass.memory_chunks chunk
            JOIN incidentcompass.memory_items item ON item.id = chunk.memory_item_id
            WHERE item.seed_owner = @owner AND item.source = 'runbooks/checkout-timeout.md' AND item.is_active
            ORDER BY chunk.chunk_position;
            """, connection);
        command.Parameters.AddWithValue("owner", owner);
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        var chunks = new List<(int, string, string)>();
        while (await reader.ReadAsync(cancellation))
        {
            chunks.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return chunks;
    }

    private static async Task AssertApiStateAsync(
        string connectionString, string owner, TriageConfiguration configuration, string expectedState)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:IncidentCompass", connectionString);
            builder.UseSetting("IncidentCompass:Memory:Seed:Owner", owner);
            builder.UseExplicitMockProviders();
            builder.ConfigureTestServices(services =>
                services.AddSingleton<ITriageConfigurationRepository>(new StaticConfiguration(configuration)));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var response = await client.GetAsync("/api/v1/health/memory-corpus", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await response.Content.ReadFromJsonAsync<MemoryCorpusSnapshot>(TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(expectedState, snapshot.State);
        Assert.Equal(expectedState == nameof(MemoryCorpusState.ChunkPolicyChanged), snapshot.RebuildRequired);
        if (snapshot.RebuildRequired)
        {
            var sync = await client.GetFromJsonAsync<MemorySeedSyncSnapshot>(
                "/api/v1/health/memory-sync", TestContext.Current.CancellationToken);
            Assert.Equal("memory_chunk_policy_changed", sync!.LastErrorCode);
        }
    }

    private sealed class StaticConfiguration(TriageConfiguration configuration) : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult(configuration);

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) => Task.FromResult(configuration);
    }
}
