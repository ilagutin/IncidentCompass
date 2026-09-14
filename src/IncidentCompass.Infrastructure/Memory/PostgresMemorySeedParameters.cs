using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace IncidentCompass.Infrastructure.Memory;

internal static class PostgresMemorySeedParameters
{
    public static void AddItem(NpgsqlCommand command, MemorySeedItem item, DateTimeOffset timestamp)
    {
        command.AddParameter("id", item.Id);
        command.AddParameter("tenant_id", item.TenantId);
        command.AddParameter("kind", item.Kind);
        command.AddParameter("source", item.Source);
        command.AddParameter("title", item.Title);
        command.AddParameter("content", item.Content);
        command.AddParameter("content_hash", item.ContentHash);
        command.AddParameter("version", item.Version);
        AddTextArray(command, "tags", item.Tags);
        command.AddParameter("service_name", item.ServiceName);
        command.AddParameter("component", item.Component);
        command.AddParameter("release_name", item.ReleaseName);
        command.AddParameter("timestamp", timestamp);
    }

    public static void AddChunk(
        NpgsqlCommand command,
        Guid memoryItemId,
        string tenantId,
        MemorySeedChunk chunk,
        DateTimeOffset timestamp)
    {
        command.AddParameter("id", chunk.Id);
        command.AddParameter("memory_item_id", memoryItemId);
        command.AddParameter("tenant_id", tenantId);
        command.AddParameter("chunk_position", chunk.Position);
        command.AddParameter("heading_path", chunk.HeadingPath);
        command.AddParameter("text", chunk.Text);
        command.AddParameter("text_hash", chunk.TextHash);
        command.AddParameter("embedding_provider", chunk.EmbeddingProvider);
        command.AddParameter("embedding_model", chunk.EmbeddingModel);
        command.AddParameter("embedding_dimensions", chunk.EmbeddingDimensions);
        command.Parameters.Add("embedding_values", NpgsqlDbType.Array | NpgsqlDbType.Real).Value = chunk.EmbeddingValues.ToArray();
        command.Parameters.AddWithValue("embedding_vector", PostgresVectorParameter.From(chunk.EmbeddingValues));
        command.AddParameter("timestamp", timestamp);
    }

    public static void AddTextArray(NpgsqlCommand command, string name, IReadOnlyCollection<string> values)
    {
        command.Parameters.Add(name, NpgsqlDbType.Array | NpgsqlDbType.Text).Value = values.ToArray();
    }
}
