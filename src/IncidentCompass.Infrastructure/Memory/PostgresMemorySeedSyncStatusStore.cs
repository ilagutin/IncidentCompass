using IncidentCompass.Infrastructure.Postgres;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IncidentCompass.Infrastructure.Memory;

internal sealed class PostgresMemorySeedSyncStatusStore(
    PostgresDataSourceProvider dataSourceProvider,
    IOptions<MemorySeedOptions> options) : IMemorySeedSyncStatusReader, IMemorySeedSyncStatusWriter
{
    public Task<MemorySeedSyncSnapshot> GetAsync(CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read memory seed synchronization status",
            () => GetCoreAsync(cancellationToken));

    public Task SaveAsync(MemorySeedSyncSnapshot snapshot, CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "save memory seed synchronization status",
            () => SaveCoreAsync(snapshot, cancellationToken));

    private async Task<MemorySeedSyncSnapshot> GetCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT status.enabled, status.runtime_resync_enabled, status.last_attempt_at_utc, status.last_success_at_utc,
                   status.active_generation,
                   CASE WHEN status.last_error_code = @chunk_policy_changed
                         AND generation.generation IS DISTINCT FROM status.active_generation
                        THEN NULL ELSE status.last_error_code END
            FROM incidentcompass.memory_seed_sync_status status
            LEFT JOIN incidentcompass.memory_corpus_generations generation
              ON generation.tenant_id = status.tenant_id AND generation.seed_owner = status.seed_owner AND generation.is_current
            WHERE status.tenant_id = @tenant_id AND status.seed_owner = @seed_owner;
            """, connection);
        AddScopeParameters(command);
        // A pass that observed the previous generation may persist its block after a concurrent
        // rebuild committed. That observation cannot block the replacement generation's health.
        command.AddParameter("chunk_policy_changed", MemoryCorpusErrorCodes.ChunkPolicyChanged);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new MemorySeedSyncSnapshot(false, false, null, null, null, null);
        }

        return new MemorySeedSyncSnapshot(
            reader.GetBoolean(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2),
            reader.IsDBNull(3) ? null : reader.GetDateTimeOffset(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private async Task SaveCoreAsync(MemorySeedSyncSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO incidentcompass.memory_seed_sync_status (
                tenant_id, seed_owner, enabled, runtime_resync_enabled, last_attempt_at_utc,
                last_success_at_utc, active_generation, last_error_code, updated_at_utc)
            VALUES (
                @tenant_id, @seed_owner, @enabled, @runtime_resync_enabled, @last_attempt_at_utc,
                @last_success_at_utc, @active_generation, @last_error_code, now())
            ON CONFLICT (tenant_id, seed_owner) DO UPDATE SET
                enabled = EXCLUDED.enabled,
                runtime_resync_enabled = EXCLUDED.runtime_resync_enabled,
                last_attempt_at_utc = EXCLUDED.last_attempt_at_utc,
                last_success_at_utc = EXCLUDED.last_success_at_utc,
                active_generation = EXCLUDED.active_generation,
                last_error_code = EXCLUDED.last_error_code,
                updated_at_utc = EXCLUDED.updated_at_utc;
            """, connection);
        AddScopeParameters(command);
        command.AddParameter("enabled", snapshot.Enabled);
        command.AddParameter("runtime_resync_enabled", snapshot.RuntimeResyncEnabled);
        command.AddParameter("last_attempt_at_utc", snapshot.LastAttemptAtUtc);
        command.AddParameter("last_success_at_utc", snapshot.LastSuccessAtUtc);
        command.AddParameter("active_generation", snapshot.ActiveGeneration);
        command.AddParameter("last_error_code", snapshot.LastErrorCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void AddScopeParameters(NpgsqlCommand command)
    {
        command.AddParameter("tenant_id", options.Value.TenantId);
        command.AddParameter("seed_owner", options.Value.Owner);
    }
}
