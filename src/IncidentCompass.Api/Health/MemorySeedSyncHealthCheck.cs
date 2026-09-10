using IncidentCompass.Infrastructure.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IncidentCompass.Api.Health;

internal sealed class MemorySeedSyncHealthCheck(IMemorySeedSyncStatusReader syncStatus) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await syncStatus.GetAsync(cancellationToken);
        var metadata = new Dictionary<string, object>
        {
            ["enabled"] = snapshot.Enabled,
            ["runtimeResyncEnabled"] = snapshot.RuntimeResyncEnabled,
            ["lastAttemptAtUtc"] = snapshot.LastAttemptAtUtc?.ToString("O") ?? string.Empty,
            ["lastSuccessAtUtc"] = snapshot.LastSuccessAtUtc?.ToString("O") ?? string.Empty,
            ["activeGeneration"] = snapshot.ActiveGeneration?.ToString() ?? string.Empty,
            ["lastErrorCode"] = snapshot.LastErrorCode ?? string.Empty
        };
        if (!snapshot.Enabled || string.IsNullOrWhiteSpace(snapshot.LastErrorCode))
        {
            return HealthCheckResult.Healthy("Memory seed synchronization is healthy.", metadata);
        }

        return HealthCheckResult.Degraded(
            DescribeDegradation(snapshot.LastErrorCode),
            data: metadata);
    }

    /// <summary>
    /// Names the corpus states an operator resolves differently. A failed pass is retried on its
    /// own; a corpus built under a different embedding route is not, because nothing about the
    /// files changed and no retry will re-embed them.
    /// </summary>
    private static string DescribeDegradation(string? errorCode) => errorCode switch
    {
        "memory_embedding_route_changed" =>
            "The memory corpus was built under a different embedding route, so memory_search finds" +
            " nothing in it. The corpus is intact; run the memory rebuild command.",
        "memory_embedding_routes_mixed" =>
            "The memory corpus holds more than one embedding route at once, so only part of it is" +
            " reachable. Run the memory rebuild command.",
        _ => "Memory seed synchronization failed; the previous corpus remains active."
    };
}
