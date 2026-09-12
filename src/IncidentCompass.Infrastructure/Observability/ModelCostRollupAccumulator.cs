using IncidentCompass.Application.Observability.CostRollup;

namespace IncidentCompass.Infrastructure.Observability;

/// <summary>
/// Folds one tenant's durable <c>ModelCall</c> ledger rows into hourly usage and spend.
/// </summary>
/// <remarks>
/// <para>
/// Two conditions have to hold before a row produces money, and both are about the row saying
/// enough rather than about the price table. The row must name the configured provider that
/// answered, because the price table is keyed on a provider and an adapter name is shared by every
/// provider it can reach; and the token counts must be the ones the provider itself reported,
/// because a locally estimated count multiplied by a price is an approximation wearing a currency
/// symbol. A row that fails either condition is still counted, its tokens still count, and it adds
/// no spend - the same honest unpriced accounting a valid row with no configured price already got.
/// </para>
/// </remarks>
internal sealed class ModelCostRollupAccumulator(IReadOnlyList<ModelPricingInterval> prices)
{
    /// <summary>
    /// The <c>usageSource</c> value that means the provider reported these token counts. Every
    /// other value, <c>estimate</c> and <c>unknown</c> included, means they were not measured.
    /// </summary>
    private const string ProviderReportedUsage = "provider";

    /// <summary>
    /// The <c>usageSource</c> value that means this system counted the tokens itself.
    /// </summary>
    private const string EstimatedUsage = "estimate";

    private const decimal TokensPerMillion = 1_000_000m;
    private readonly Dictionary<DateTimeOffset, MutableCostRollupHour> hours = [];
    private readonly Dictionary<(string Provider, string Model), ModelPricingInterval[]> pricesByIdentity =
        prices
            .GroupBy(static price => (price.Provider, price.Model))
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray());

    public void Add(DateTimeOffset createdAtUtc, string? rationale)
    {
        var hourUtc = new DateTimeOffset(
            createdAtUtc.Year,
            createdAtUtc.Month,
            createdAtUtc.Day,
            createdAtUtc.Hour,
            0,
            0,
            TimeSpan.Zero);
        if (!hours.TryGetValue(hourUtc, out var hour))
        {
            hour = new MutableCostRollupHour();
            hours.Add(hourUtc, hour);
        }

        hour.CallCount++;
        if (!ModelCallUsageParser.TryParse(rationale, out var usage))
        {
            hour.UnpricedCallCount++;
            return;
        }

        hour.InputTokens += usage!.InputTokens;
        hour.OutputTokens += usage.OutputTokens;
        hour.TotalTokens += usage.TotalTokens;
        if (string.Equals(usage.UsageSource, EstimatedUsage, StringComparison.Ordinal))
        {
            hour.EstimatedUsageCallCount++;
            hour.EstimatedUsageTotalTokens += usage.TotalTokens;
        }

        if (!IsPriceable(usage) || FindPrice(usage.ProviderId!, usage.Model, createdAtUtc) is not { } price)
        {
            hour.UnpricedCallCount++;
            return;
        }

        hour.PricedCallCount++;
        var amount =
            usage.InputTokens / TokensPerMillion * price.InputTokenPricePerMillion +
            usage.OutputTokens / TokensPerMillion * price.OutputTokenPricePerMillion;
        hour.SpendByCurrency[price.Currency] =
            hour.SpendByCurrency.GetValueOrDefault(price.Currency) + amount;
    }

    public IReadOnlyList<CostRollupHour> Build() =>
        hours
            .OrderBy(static item => item.Key)
            .Select(static item => item.Value.ToResponse(item.Key))
            .ToArray();

    /// <summary>
    /// Whether this row says enough about itself to be turned into money at all.
    /// </summary>
    private static bool IsPriceable(ModelCallUsage usage) =>
        usage.ProviderId is not null &&
        string.Equals(usage.UsageSource, ProviderReportedUsage, StringComparison.Ordinal);

    /// <summary>
    /// The single price interval covering this call, or <see langword="null"/> when none or more
    /// than one does. A tie is ambiguity, and ambiguity is unpriced rather than arbitrated.
    /// </summary>
    private ModelPricingInterval? FindPrice(string providerId, string model, DateTimeOffset createdAtUtc)
    {
        var matchingPrices = pricesByIdentity.TryGetValue((providerId, model), out var candidates)
            ? candidates.Where(price => price.Contains(createdAtUtc)).Take(2).ToArray()
            : [];
        return matchingPrices.Length == 1 ? matchingPrices[0] : null;
    }
}
