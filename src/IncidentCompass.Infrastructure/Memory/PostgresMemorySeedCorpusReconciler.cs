using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Memory;

internal static class PostgresMemorySeedCorpusReconciler
{
    public static async Task ReconcileAsync(
        PostgresDataSourceProvider dataSourceProvider,
        DateTimeOffset timestamp,
        MemorySeedCorpus corpus,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpus.Owner);
        if (corpus.Entries.Select(static entry => entry.Item.Source).Distinct(StringComparer.Ordinal).Count() != corpus.Entries.Count)
        {
            throw new InvalidOperationException("A memory seed corpus cannot contain duplicate sources.");
        }

        if (corpus.Entries.Any(entry => !string.Equals(
            entry.Item.TenantId, corpus.TenantId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A memory seed corpus cannot span tenants.");
        }

        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockCorpusAsync(connection, transaction, corpus, cancellationToken);
            await EnsureCompleteScanAsync(connection, transaction, corpus, cancellationToken);
            foreach (var entry in corpus.Entries)
            {
                await ReconcileEntryAsync(connection, transaction, timestamp, corpus, entry, cancellationToken);
            }

            await DeactivateMissingAsync(connection, transaction, timestamp, corpus, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task EnsureCompleteScanAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemorySeedCorpus corpus,
        CancellationToken cancellationToken)
    {
        if (corpus.Entries.Count == 0)
        {
            throw new InvalidOperationException("Memory seed scan did not find any files.");
        }

        await using var command = new NpgsqlCommand("""
            SELECT source
            FROM incidentcompass.memory_items
            WHERE tenant_id = @tenant_id AND seed_owner = @seed_owner
              AND seed_managed = true AND is_active = true;
            """, connection, transaction);
        command.AddParameter("tenant_id", corpus.TenantId);
        command.AddParameter("seed_owner", corpus.Owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var missingPrefixes = new SortedSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var prefix = reader.GetString(0).Split('/', 2)[0];
            if (!corpus.ObservedSourcePrefixes.Contains(prefix))
            {
                missingPrefixes.Add(prefix);
            }
        }

        if (missingPrefixes.Count > 0)
        {
            throw new InvalidOperationException(
                "Memory seed scan is missing directories required by the current corpus: " +
                string.Join(", ", missingPrefixes) + ".");
        }
    }

    private static async Task ReconcileEntryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset timestamp,
        MemorySeedCorpus corpus,
        MemorySeedEntry entry,
        CancellationToken cancellationToken)
    {
        if (await PostgresMemorySeedWriter.CurrentSeedMatchesAsync(
            connection, transaction, corpus.Owner, entry.Item, cancellationToken))
        {
            await PostgresMemorySeedItemWriter.MarkCurrentAsync(
                connection, transaction, corpus, entry.Item.Source, timestamp, cancellationToken);
            return;
        }

        if (entry.Chunks.Count == 0)
        {
            throw new InvalidOperationException($"Memory seed '{entry.Item.Source}' changed during reconciliation.");
        }

        var itemId = await PostgresMemorySeedItemWriter.FindSeedItemIdAsync(
            connection, transaction, corpus.Owner, entry.Item, cancellationToken);
        if (itemId is null)
        {
            itemId = await PostgresMemorySeedItemWriter.InsertAsync(
                connection, transaction, timestamp, corpus, entry.Item, cancellationToken);
        }
        else
        {
            await PostgresMemorySeedItemWriter.UpdateAsync(
                connection, transaction, timestamp, corpus, itemId.Value, entry.Item, cancellationToken);
        }

        await PostgresMemorySeedChunkWriter.ReplaceAsync(
            connection, transaction, itemId.Value, entry.Item, entry.Chunks, timestamp, cancellationToken);
    }

    private static async Task LockCorpusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        MemorySeedCorpus corpus,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@identity, 0));",
            connection, transaction);
        command.AddParameter("identity", corpus.Owner + ":" + corpus.TenantId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeactivateMissingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset timestamp,
        MemorySeedCorpus corpus,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            UPDATE incidentcompass.memory_items
            SET is_active = false, superseded_at_utc = @timestamp, updated_at_utc = @timestamp
            WHERE tenant_id = @tenant_id AND seed_owner = @seed_owner
              AND seed_managed = true AND is_active = true
              AND NOT (source = ANY(@active_sources));
            """, connection, transaction);
        command.AddParameter("tenant_id", corpus.TenantId);
        command.AddParameter("seed_owner", corpus.Owner);
        PostgresMemorySeedParameters.AddTextArray(
            command, "active_sources", corpus.Entries.Select(static entry => entry.Item.Source).ToArray());
        command.AddParameter("timestamp", timestamp);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
