using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// Reads what one owner's corpus holds: the generation flagged current, and the vector spaces its
/// active chunks are actually in. Counts and identifiers only; no chunk text is selected.
/// </summary>
internal static class PostgresMemoryCorpusInventoryReader
{
    public static async Task<MemoryCorpusInventory> ReadAsync(
        PostgresDataSourceProvider dataSourceProvider,
        string tenantId,
        string owner,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        var current = await ReadCurrentGenerationAsync(connection, tenantId, owner, cancellationToken);
        var identities = await ReadActiveIdentitiesAsync(connection, tenantId, owner, cancellationToken);
        var counts = await ReadActiveCountsAsync(connection, tenantId, owner, cancellationToken);
        return new MemoryCorpusInventory(current, identities, counts.Items, counts.Chunks);
    }

    private static async Task<MemoryCorpusGeneration?> ReadCurrentGenerationAsync(
        NpgsqlConnection connection,
        string tenantId,
        string owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT generation, route_id, provider_id, embedding_provider, embedding_model,
                   embedding_dimensions, item_count, chunk_count, published_at_utc
            FROM incidentcompass.memory_corpus_generations
            WHERE tenant_id = @tenant_id AND seed_owner = @seed_owner AND is_current;
            """, connection);
        AddScope(command, tenantId, owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MemoryCorpusGeneration(
            reader.GetGuid(0),
            new MemoryCorpusIdentity(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5)),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetDateTimeOffset(8));
    }

    private static async Task<IReadOnlyList<MemoryCorpusIdentity>> ReadActiveIdentitiesAsync(
        NpgsqlConnection connection,
        string tenantId,
        string owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT DISTINCT chunk.embedding_provider, chunk.embedding_model, chunk.embedding_dimensions
            FROM incidentcompass.memory_chunks AS chunk
            JOIN incidentcompass.memory_items AS item ON item.id = chunk.memory_item_id
            WHERE item.tenant_id = @tenant_id AND item.seed_owner = @seed_owner
              AND item.seed_managed = true AND item.is_active = true
            ORDER BY chunk.embedding_provider, chunk.embedding_model, chunk.embedding_dimensions;
            """, connection);
        AddScope(command, tenantId, owner);
        var identities = new List<MemoryCorpusIdentity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            identities.Add(new MemoryCorpusIdentity(
                RouteId: null,
                ProviderId: null,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2)));
        }

        return identities;
    }

    private static async Task<(int Items, int Chunks)> ReadActiveCountsAsync(
        NpgsqlConnection connection,
        string tenantId,
        string owner,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(DISTINCT item.id)::integer, count(chunk.id)::integer
            FROM incidentcompass.memory_items AS item
            LEFT JOIN incidentcompass.memory_chunks AS chunk ON chunk.memory_item_id = item.id
            WHERE item.tenant_id = @tenant_id AND item.seed_owner = @seed_owner
              AND item.seed_managed = true AND item.is_active = true;
            """, connection);
        AddScope(command, tenantId, owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private static void AddScope(NpgsqlCommand command, string tenantId, string owner)
    {
        command.AddParameter("tenant_id", tenantId);
        command.AddParameter("seed_owner", owner);
    }
}
