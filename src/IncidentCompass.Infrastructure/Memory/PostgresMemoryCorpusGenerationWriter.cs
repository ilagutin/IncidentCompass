using System.Globalization;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// Publishes a corpus generation inside the reconciliation transaction and refuses to publish one
/// that would leave the owner holding two vector spaces.
/// </summary>
/// <remarks>
/// Both halves run after every item and chunk write and before the commit, which is what makes a
/// generation current only once the whole corpus behind it exists. A failure here rolls the
/// transaction back with the previous generation still flagged current and still retrievable.
/// </remarks>
internal static class PostgresMemoryCorpusGenerationWriter
{
    public static async Task PublishAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset timestamp,
        MemorySeedCorpus corpus,
        CancellationToken cancellationToken)
    {
        await VerifySingleIdentityAsync(connection, transaction, corpus, cancellationToken);
        var counts = await ReadCountsAsync(connection, transaction, corpus, cancellationToken);
        await ClearPreviousCurrentAsync(connection, transaction, corpus, cancellationToken);
        await InsertAsync(connection, transaction, timestamp, corpus, counts, cancellationToken);
    }

    private static async Task VerifySingleIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemorySeedCorpus corpus,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT chunk.embedding_provider, chunk.embedding_model, chunk.embedding_dimensions
            FROM incidentcompass.memory_chunks AS chunk
            JOIN incidentcompass.memory_items AS item ON item.id = chunk.memory_item_id
            WHERE item.tenant_id = @tenant_id AND item.seed_owner = @seed_owner
              AND item.seed_managed = true AND item.is_active = true;
            """, connection, transaction);
        AddScope(command, corpus);
        var identities = new List<MemoryCorpusIdentity>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                identities.Add(new MemoryCorpusIdentity(
                    corpus.Identity.RouteId,
                    corpus.Identity.ProviderId,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2)));
            }
        }

        if (identities.Count == 1 && identities[0].Matches(corpus.Identity))
        {
            return;
        }

        // Named by count and by model, never by vector. An operator needs to know that the corpus
        // would have straddled two embedding models, not what any of it contains.
        throw new InvalidOperationException(
            "Memory seed reconciliation would leave owner '" + corpus.Owner + "' holding " +
            identities.Count.ToString(CultureInfo.InvariantCulture) +
            " embedding identities instead of the one it is publishing (" +
            corpus.Identity.EmbeddingModel + "/" +
            corpus.Identity.EmbeddingDimensions.ToString(CultureInfo.InvariantCulture) +
            "). The previous corpus is unchanged; run the memory rebuild command to move every seed" +
            " to the configured embedding route.");
    }

    private static async Task<(int Items, int Chunks)> ReadCountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemorySeedCorpus corpus,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(DISTINCT item.id)::integer, count(chunk.id)::integer
            FROM incidentcompass.memory_items AS item
            LEFT JOIN incidentcompass.memory_chunks AS chunk ON chunk.memory_item_id = item.id
            WHERE item.tenant_id = @tenant_id AND item.seed_owner = @seed_owner
              AND item.seed_managed = true AND item.is_active = true;
            """, connection, transaction);
        AddScope(command, corpus);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static async Task ClearPreviousCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemorySeedCorpus corpus,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE incidentcompass.memory_corpus_generations
            SET is_current = false
            WHERE tenant_id = @tenant_id AND seed_owner = @seed_owner
              AND is_current AND generation <> @generation;
            """, connection, transaction);
        AddScope(command, corpus);
        command.AddParameter("generation", corpus.Generation);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset timestamp,
        MemorySeedCorpus corpus,
        (int Items, int Chunks) counts,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.memory_corpus_generations (
                generation, tenant_id, seed_owner, route_id, provider_id,
                embedding_provider, embedding_model, embedding_dimensions,
                item_count, chunk_count, is_current, published_at_utc)
            VALUES (
                @generation, @tenant_id, @seed_owner, @route_id, @provider_id,
                @embedding_provider, @embedding_model, @embedding_dimensions,
                @item_count, @chunk_count, true, @published_at_utc)
            ON CONFLICT (generation) DO UPDATE SET
                route_id = EXCLUDED.route_id,
                provider_id = EXCLUDED.provider_id,
                embedding_provider = EXCLUDED.embedding_provider,
                embedding_model = EXCLUDED.embedding_model,
                embedding_dimensions = EXCLUDED.embedding_dimensions,
                item_count = EXCLUDED.item_count,
                chunk_count = EXCLUDED.chunk_count,
                is_current = true,
                published_at_utc = EXCLUDED.published_at_utc;
            """, connection, transaction);
        AddScope(command, corpus);
        command.AddParameter("generation", corpus.Generation);
        command.AddParameter("route_id", corpus.Identity.RouteId);
        command.AddParameter("provider_id", corpus.Identity.ProviderId);
        command.AddParameter("embedding_provider", corpus.Identity.EmbeddingProvider);
        command.AddParameter("embedding_model", corpus.Identity.EmbeddingModel);
        command.AddParameter("embedding_dimensions", corpus.Identity.EmbeddingDimensions);
        command.AddParameter("item_count", counts.Items);
        command.AddParameter("chunk_count", counts.Chunks);
        command.AddParameter("published_at_utc", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddScope(NpgsqlCommand command, MemorySeedCorpus corpus)
    {
        command.AddParameter("tenant_id", corpus.TenantId);
        command.AddParameter("seed_owner", corpus.Owner);
    }
}
