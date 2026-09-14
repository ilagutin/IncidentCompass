using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Memory;

internal static class PostgresMemorySeedChunkWriter
{
    public static async Task ReplaceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        MemorySeedItem item,
        IReadOnlyList<MemorySeedChunk> chunks,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        await using (var delete = new NpgsqlCommand(
            "DELETE FROM incidentcompass.memory_chunks WHERE memory_item_id = @memory_item_id;",
            connection,
            transaction))
        {
            delete.AddParameter("memory_item_id", itemId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var chunk in chunks)
        {
            await InsertAsync(connection, transaction, itemId, item.TenantId, chunk, timestamp, cancellationToken);
        }
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        string tenantId,
        MemorySeedChunk chunk,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.memory_chunks (
                id, memory_item_id, tenant_id, chunk_position, heading_path, text, text_hash,
                embedding_provider, embedding_model, embedding_dimensions,
                embedding_values, embedding_vector, created_at_utc)
            VALUES (
                @id, @memory_item_id, @tenant_id, @chunk_position, @heading_path, @text, @text_hash,
                @embedding_provider, @embedding_model, @embedding_dimensions,
                @embedding_values, @embedding_vector, @timestamp);
            """, connection, transaction);
        PostgresMemorySeedParameters.AddChunk(command, itemId, tenantId, chunk, timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
