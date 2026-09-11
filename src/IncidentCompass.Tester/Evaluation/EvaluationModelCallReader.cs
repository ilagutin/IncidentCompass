using System.Text.Json;

namespace IncidentCompass.Tester.Evaluation;

internal static class EvaluationModelCallReader
{
    public static EvaluationModelCallReadResult Read(
        FaultLedgerResponse ledger,
        Guid? jobId,
        int? attempt)
    {
        var calls = new List<EvaluationModelCallMetadata>();
        var malformedRows = 0;
        if (jobId is null || attempt is null)
        {
            return new(calls, malformedRows);
        }

        foreach (var item in ledger.Events.Where(item =>
                     item.EventType == "ModelCall" &&
                     item.JobId == jobId.Value &&
                     item.Attempt == attempt.Value))
        {
            if (!TryRead(item.Rationale, out var call))
            {
                malformedRows++;
                continue;
            }

            calls.Add(call!);
        }

        return new(calls, malformedRows);
    }

    private static bool TryRead(string? rationale, out EvaluationModelCallMetadata? call)
    {
        call = null;
        if (string.IsNullOrWhiteSpace(rationale))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rationale);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryReadString(root, "routeId", out var routeId) ||
                !TryReadString(root, "model", out var model) ||
                !TryReadString(root, "provider", out var provider) ||
                !TryReadString(root, "usageSource", out var usageSource) ||
                !TryReadInt32(root, "inputTokens", out var inputTokens) ||
                !TryReadInt32(root, "outputTokens", out var outputTokens) ||
                !TryReadInt32(root, "totalTokens", out var totalTokens) ||
                !TryReadInt64(root, "durationMs", out var durationMs))
            {
                return false;
            }

            call = new EvaluationModelCallMetadata(
                routeId,
                model,
                provider,
                ReadOptionalString(root, "providerId"),
                usageSource,
                inputTokens,
                outputTokens,
                totalTokens,
                durationMs);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // An optional identity never makes a row malformed. A ledger row written before the backend
    // recorded the configured provider id, and a row whose route named no provider, both state no
    // payer; rejecting either would drop that call's real latency and token counts out of the run's
    // totals over a field that carries none of them. An unusable value is recorded as absent for the
    // same reason, because this reader never invents a payer the row did not state.
    private static string? ReadOptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()
            : null;

    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryReadInt32(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) && property.TryGetInt32(out value) && value >= 0;
    }

    private static bool TryReadInt64(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) && property.TryGetInt64(out value) && value >= 0;
    }
}
