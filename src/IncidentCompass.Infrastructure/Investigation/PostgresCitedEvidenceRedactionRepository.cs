using IncidentCompass.Application.Investigation.Reports.Redaction;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Investigation;

/// <summary>
/// Reads <c>triage_artifacts.redaction_applied</c> for the artifacts a report cites. The column is
/// what the redaction boundary recorded when it ran; nothing here inspects payload text, because a
/// redacted value and connector text that already contained the same literal are indistinguishable
/// once stored.
/// <para>
/// Every parsed citation is looked up. The read deliberately has no cap on how many artifacts it
/// will answer for, because truncation here can only ever understate: dropping citations can turn a
/// report whose evidence was redacted into one that says nothing, and the model orders its own
/// evidence array, so it would choose which citations survived. Nothing upstream bounds the number
/// of evidence entries either. What is bounded instead is the shape of each round trip - the ids go
/// to the server in fixed-size batches as a single array parameter, so the parameter list never
/// grows with the citation count - and every batch is queried, so no batching arrangement can change
/// the answer.
/// </para>
/// </summary>
internal sealed class PostgresCitedEvidenceRedactionRepository(
    PostgresDataSourceProvider dataSourceProvider) : ICitedEvidenceRedactionRepository
{
    /// <summary>
    /// How many artifact ids travel in one <c>= ANY(...)</c> array parameter. This bounds the size of
    /// a single statement, not the set of citations that get an answer: the batches partition the
    /// parsed ids and all of them are executed.
    /// </summary>
    private const int ArtifactIdBatchSize = 500;

    public Task<IReadOnlyList<CitedEvidenceRedaction>> ReadCitedAsync(
        Guid jobId,
        int attempt,
        IReadOnlyCollection<string> referenceIds,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<Guid>();
        var artifactIds = new List<Guid>();
        foreach (var referenceId in referenceIds)
        {
            if (ReportEvidenceArtifactReference.TryParse(referenceId, out var artifactId) &&
                seen.Add(artifactId))
            {
                artifactIds.Add(artifactId);
            }
        }

        return artifactIds.Count == 0
            ? Task.FromResult<IReadOnlyList<CitedEvidenceRedaction>>([])
            : PostgresOperation.ExecuteAsync(
                "read cited evidence redaction outcomes",
                () => ReadAsync(jobId, attempt, artifactIds, cancellationToken));
    }

    private async Task<IReadOnlyList<CitedEvidenceRedaction>> ReadAsync(
        Guid jobId,
        int attempt,
        IReadOnlyList<Guid> artifactIds,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        var cited = new List<CitedEvidenceRedaction>();
        foreach (var batch in artifactIds.Chunk(ArtifactIdBatchSize))
        {
            await ReadBatchAsync(connection, jobId, attempt, batch, cited, cancellationToken);
        }

        return cited;
    }

    private static async Task ReadBatchAsync(
        NpgsqlConnection connection,
        Guid jobId,
        int attempt,
        Guid[] artifactIds,
        List<CitedEvidenceRedaction> cited,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id, redaction_applied
            FROM incidentcompass.triage_artifacts
            WHERE job_id = @job_id
              AND (attempt IS NULL OR attempt = @attempt)
              AND id = ANY(@artifact_ids);
            """, connection);
        command.AddParameter("job_id", jobId);
        command.AddParameter("attempt", attempt);
        command.AddParameter("artifact_ids", artifactIds);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cited.Add(new CitedEvidenceRedaction(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetBoolean(1)));
        }
    }
}
