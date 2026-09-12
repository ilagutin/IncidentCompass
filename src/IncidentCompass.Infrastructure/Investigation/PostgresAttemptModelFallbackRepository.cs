using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Investigation.Reports.Fallback;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Investigation;

/// <summary>
/// Derives an attempt's fail-overs from its <c>ModelCall</c> triage-ledger rows: a successful call
/// whose metadata carries <c>fallbackForRouteId</c> was answered by a route other than the one it
/// was configured on.
/// </summary>
/// <remarks>
/// <para>
/// The ledger is the single source, for the same reason model provenance reads it: every model call
/// in an attempt that reaches publication has a durable row there, because a call whose ledger
/// append fails ends the attempt. A second accumulator kept alongside the run could drift from it,
/// and a marker on a report is exactly the kind of claim that must not.
/// </para>
/// <para>
/// The metadata is parsed here rather than in SQL. <c>rationale</c> is a text column shared with
/// every other event type, most of which hold a plain sentence, so a JSON operator applied to it
/// would depend on the planner filtering by event type before evaluating the cast.
/// </para>
/// </remarks>
internal sealed class PostgresAttemptModelFallbackRepository(
    PostgresDataSourceProvider dataSourceProvider) : IAttemptModelFallbackRepository
{
    /// <summary>
    /// The largest <c>ModelCall</c> rationale this reader will parse, matching the bound the
    /// provenance and cost-rollup readers apply to the same rows.
    /// </summary>
    private const int MaximumMetadataBytes = 8192;

    /// <summary>
    /// How many <c>ModelCall</c> rows one attempt is read for. An attempt is bounded by its turn,
    /// worker and token budgets long before this, so the cap is a guard on a pathological row count
    /// rather than a sampling decision, and it cannot understate in the direction that matters: the
    /// rows are ordered by ledger id, so the earliest calls, including any fail-over among them, are
    /// the ones inside the cap.
    /// </summary>
    private const int MaximumModelCallRows = 500;

    private const string SuccessOutcome = "success";

    private const string ModelCallsSql = """
        SELECT ledger.rationale
        FROM incidentcompass.triage_ledger AS ledger
        WHERE ledger.job_id = @job_id
          AND ledger.attempt = @attempt
          AND ledger.event_type = 'ModelCall'
        ORDER BY ledger.id
        LIMIT @row_limit;
        """;

    public Task<IReadOnlyList<AttemptModelFallback>> ReadCurrentAttemptAsync(
        Guid jobId,
        int attempt,
        CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "read current-attempt model fallbacks",
            () => ReadAsync(jobId, attempt, cancellationToken));

    private async Task<IReadOnlyList<AttemptModelFallback>> ReadAsync(
        Guid jobId,
        int attempt,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSourceProvider.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ModelCallsSql, connection);
        command.AddParameter("job_id", jobId);
        command.AddParameter("attempt", attempt);
        command.AddParameter("row_limit", MaximumModelCallRows);

        // Rows arrive ordered by ledger id, so first insertion is first-answered order.
        var seen = new HashSet<AttemptModelFallback>();
        var fallbacks = new List<AttemptModelFallback>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var rationale = reader.IsDBNull(0) ? null : reader.GetString(0);
            if (TryReadSuccessfulFallback(rationale) is { } fallback && seen.Add(fallback))
            {
                fallbacks.Add(fallback);
            }
        }

        return fallbacks;
    }

    /// <summary>
    /// Returns the pairing a row records, or <see langword="null" /> when the row is not a
    /// successful fail-over.
    /// </summary>
    /// <remarks>
    /// A row this reader cannot parse is skipped rather than raised. The claim being derived is
    /// "something failed over", and the direction that would harm a reader is a report that stays
    /// silent about a fail-over that happened - which an unparsable row cannot cause here, because a
    /// row written by the appender is parsable and the report's model provenance, derived from these
    /// same rows inside the publish transaction, already refuses to publish at all when one is not.
    /// </remarks>
    private static AttemptModelFallback? TryReadSuccessfulFallback(string? rationale)
    {
        if (rationale is null ||
            rationale.Length > MaximumMetadataBytes ||
            Encoding.UTF8.GetByteCount(rationale) > MaximumMetadataBytes)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rationale, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryReadIdentity(root, "outcome", out var outcome) ||
                !string.Equals(outcome, SuccessOutcome, StringComparison.Ordinal) ||
                !TryReadIdentity(root, "fallbackForRouteId", out var routeId) ||
                !TryReadIdentity(root, "routeId", out var fallbackRouteId))
            {
                return null;
            }

            return new AttemptModelFallback(routeId!, fallbackRouteId!);
        }
    }

    private static bool TryReadIdentity(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }
}
