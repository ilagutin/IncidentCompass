using System.Text.Json;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Turns a benchmark query's fixture signal into the trigger signal a <c>memory_search</c> context
/// carries, so the band the tool reports is decided against the fault query the production path builds.
/// </summary>
/// <remarks>
/// The fixture carries no summary, exactly like a structured signal that arrives without one, so the
/// summary is synthesized here by the production <c>SummarySynthesizer</c> from the same fields intake
/// passes it. Nothing about the fault query is written by the benchmark itself.
/// </remarks>
internal static class MemoryRetrievalBenchmarkTriggerSignal
{
    /// <summary>The query's trigger signal, or null for a corpus that predates signals.</summary>
    public static Signal? For(string tenantId, MemoryRetrievalBenchmarkQuery query) =>
        query.Signal is null ? null : Create(tenantId, query.Signal);

    public static Signal Create(string tenantId, MemoryRetrievalBenchmarkSignal signal)
    {
        var summary = SummarySynthesizer.ForStructuredSignal(
            signal.ServiceName,
            signal.OperationName,
            signal.HttpRoute,
            signal.ErrorType,
            signal.ErrorMessage);
        return Build(
            tenantId,
            signal.ServiceName,
            summary,
            signal.ErrorType,
            signal.ErrorMessage,
            signal.OperationName,
            signal.HttpRoute);
    }

    /// <summary>
    /// A signal whose fault query is exactly <paramref name="modelQuery" />: the text is carried as the
    /// service name and every other part is blank, so the fault query composes to it unchanged. The
    /// benchmark uses it for one thing only, to reproduce the rule this release replaced, under which
    /// the band was decided against the model's own query. It is never an intake shape.
    /// </summary>
    public static Signal ConfirmingAgainstTheModelQuery(string tenantId, string modelQuery) =>
        Build(tenantId, modelQuery, string.Empty, null, null, null, null);

    private static Signal Build(
        string tenantId,
        string serviceName,
        string summary,
        string? errorType,
        string? errorMessage,
        string? operationName,
        string? httpRoute)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new Signal(
            Guid.Parse("30000000-0000-0000-0000-000000000004"),
            tenantId,
            "benchmark",
            Guid.Parse("30000000-0000-0000-0000-000000000002"),
            "benchmark-fingerprint",
            1,
            FingerprintStrength.Strong,
            true,
            null,
            false,
            null,
            null,
            null,
            null,
            null,
            serviceName,
            "production",
            operationName,
            "Error",
            errorType,
            errorMessage,
            summary,
            null,
            null,
            httpRoute,
            null,
            null,
            EmptyObject(),
            EmptyObject(),
            now,
            now,
            null);
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
