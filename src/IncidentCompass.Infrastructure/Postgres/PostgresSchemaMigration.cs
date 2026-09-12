using Npgsql;

namespace IncidentCompass.Infrastructure.Postgres;

internal sealed record PostgresSchemaMigration(
    int Version,
    string Name,
    IReadOnlyList<string> ScriptNames,
    bool AcceptsReleasedLegacyChecksums = false)
{
    public async Task ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (var scriptName in ScriptNames)
        {
            await PostgresMigrationScriptExecutor.ExecuteAsync(
                connection,
                transaction,
                scriptName,
                cancellationToken);
        }
    }
}
