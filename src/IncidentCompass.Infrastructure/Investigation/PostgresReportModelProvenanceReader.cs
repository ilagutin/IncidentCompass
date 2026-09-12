using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Infrastructure.Postgres;
using Npgsql;

namespace IncidentCompass.Infrastructure.Investigation;

/// <summary>
/// Derives a publishing report's model provenance from the <c>ModelCall</c> triage-ledger rows of
/// the job attempt that is publishing it.
/// </summary>
/// <remarks>
/// <para>
/// The ledger is the single source: it already records, per call, which route was used and which
/// provider and model actually answered, and every model call in a successful attempt has a
/// durable row there, because a model call whose ledger append fails ends the attempt. Reading it
/// here inside the publish transaction makes the stored provenance a projection of that one truth
/// rather than a second accumulator that could drift away from it.
/// </para>
/// <para>
/// Only successful calls are counted. A failed call produced nothing the report is built on, and
/// its accounting is written against the attempt that failed rather than the one that publishes.
/// </para>
/// </remarks>
internal static class PostgresReportModelProvenanceReader
{
    /// <summary>
    /// The largest <c>ModelCall</c> rationale this reader will parse. It matches the bound the
    /// cost-rollup reader applies to the same rows, so an oversized row is refused at both readers
    /// rather than parsed at one of them.
    /// </summary>
    private const int MaximumMetadataBytes = 8192;

    private const string SuccessOutcome = "success";

    private const string ModelCallsSql = """
        SELECT ledger.id, ledger.role, ledger.rationale
        FROM incidentcompass.triage_ledger AS ledger
        WHERE ledger.job_id = @job_id
          AND ledger.attempt = @attempt
          AND ledger.event_type = 'ModelCall'
        ORDER BY ledger.id;
        """;

    public static async Task<IReadOnlyList<TriageReportModelParticipant>> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TriageJob job,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(ModelCallsSql, connection, transaction);
        command.AddParameter("job_id", job.Id);
        command.AddParameter("attempt", job.Attempt);

        // Rows arrive ordered by ledger id, so first insertion into `order` is first-answered order.
        var callCounts = new Dictionary<ModelCallParticipantKey, int>();
        var order = new List<ModelCallParticipantKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var role = reader.IsDBNull(1) ? null : reader.GetString(1);
            var rationale = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (ReadSuccessfulCall(reader.GetInt64(0), role, rationale) is not { } key)
            {
                continue;
            }

            if (callCounts.TryGetValue(key, out var callCount))
            {
                callCounts[key] = callCount + 1;
                continue;
            }

            callCounts[key] = 1;
            order.Add(key);
        }

        return order
            .Select(key => new TriageReportModelParticipant(
                key.CallKind,
                key.Role,
                key.RouteId,
                key.Provider,
                key.Model,
                callCounts[key],
                key.ProviderId))
            .ToArray();
    }

    /// <summary>
    /// Reads one <c>ModelCall</c> row, or returns <see langword="null"/> when the call did not
    /// succeed and so contributed nothing to the report.
    /// </summary>
    /// <remarks>
    /// A row that cannot be read at all is not skipped. The appender that writes this payload and
    /// the readers of it are the only parties to that shape, so an unreadable row means the ledger
    /// no longer says what happened, and quietly leaving that call out would publish a report
    /// claiming fewer models than answered it. The failure names the ledger row and nothing from
    /// inside it.
    /// <para>
    /// The configured provider id is read as optional. It is what an adapter name cannot be - the
    /// identity of the declared provider that answered - but a row written before it was recorded
    /// carries no such claim, and refusing to publish over that would turn a rolling deployment
    /// into a failed attempt.
    /// </para>
    /// </remarks>
    private static ModelCallParticipantKey? ReadSuccessfulCall(long ledgerId, string? role, string? rationale)
    {
        using var document = ParseMetadata(ledgerId, rationale);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !TryReadIdentity(root, "outcome", out var outcome) ||
            !TryReadIdentity(root, "kind", out var callKind) ||
            !TryReadIdentity(root, "routeId", out var routeId) ||
            !TryReadIdentity(root, "provider", out var provider) ||
            !TryReadIdentity(root, "model", out var model))
        {
            throw UnreadableRow(ledgerId);
        }

        var providerId = TryReadIdentity(root, "providerId", out var declaredProviderId)
            ? declaredProviderId
            : null;
        return string.Equals(outcome, SuccessOutcome, StringComparison.Ordinal)
            ? new ModelCallParticipantKey(callKind!, role, routeId!, provider!, providerId, model!)
            : null;
    }

    private static JsonDocument ParseMetadata(long ledgerId, string? rationale)
    {
        if (rationale is null ||
            rationale.Length > MaximumMetadataBytes ||
            Encoding.UTF8.GetByteCount(rationale) > MaximumMetadataBytes)
        {
            throw UnreadableRow(ledgerId);
        }

        try
        {
            return JsonDocument.Parse(rationale, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        }
        catch (JsonException)
        {
            throw UnreadableRow(ledgerId);
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

    private static InvalidOperationException UnreadableRow(long ledgerId) =>
        new($"Triage ledger ModelCall row {ledgerId} does not carry readable model-call metadata, " +
            "so this report's model provenance cannot be derived.");
}
