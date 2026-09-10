using IncidentCompass.Application.Observability.CostRollup;

namespace IncidentCompass.Infrastructure.Observability;

internal sealed class MutableCostRollupHour
{
    public long CallCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long PricedCallCount { get; set; }
    public long UnpricedCallCount { get; set; }
    public long EstimatedUsageCallCount { get; set; }
    public long EstimatedUsageTotalTokens { get; set; }
    public Dictionary<string, decimal> SpendByCurrency { get; } = new(StringComparer.Ordinal);

    public CostRollupHour ToResponse(DateTimeOffset hourUtc) =>
        new(
            hourUtc,
            CallCount,
            InputTokens,
            OutputTokens,
            TotalTokens,
            PricedCallCount,
            UnpricedCallCount,
            EstimatedUsageCallCount,
            EstimatedUsageTotalTokens,
            SpendByCurrency
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => new CostRollupSpend(item.Key, item.Value))
                .ToArray());
}
