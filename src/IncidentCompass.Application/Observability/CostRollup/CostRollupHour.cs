namespace IncidentCompass.Application.Observability.CostRollup;

/// <summary>
/// One UTC hour of recorded model usage for one tenant.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SpendTotals"/> is built only from calls whose token counts the provider itself
/// reported. A call whose usage this system estimated locally is counted, its tokens are counted,
/// and it is never priced, so a spend figure here is never part measurement and part approximation.
/// <see cref="EstimatedUsageCallCount"/> and <see cref="EstimatedUsageTotalTokens"/> say how much of
/// the hour that excluded, which is what turns "spend looks low" into an answerable question.
/// </para>
/// <para>
/// The two estimated-usage totals are subsets of <see cref="CallCount"/> and
/// <see cref="TotalTokens"/>, not additions to them, and every estimated-usage call is also counted
/// in <see cref="UnpricedCallCount"/>.
/// </para>
/// </remarks>
/// <param name="HourUtc">The start of the UTC hour these totals cover.</param>
/// <param name="CallCount">Every durable model-call row in the hour, successful or not.</param>
/// <param name="InputTokens">Input tokens across the calls whose usage metadata was valid.</param>
/// <param name="OutputTokens">Output tokens across the calls whose usage metadata was valid.</param>
/// <param name="TotalTokens">Total tokens across the calls whose usage metadata was valid.</param>
/// <param name="PricedCallCount">Calls that produced spend, all of them provider-reported.</param>
/// <param name="UnpricedCallCount">Calls that produced no spend, for any reason.</param>
/// <param name="EstimatedUsageCallCount">Calls whose token counts this system estimated rather than received.</param>
/// <param name="EstimatedUsageTotalTokens">How many of the hour's total tokens came from those calls.</param>
/// <param name="SpendTotals">Exact spend per currency, never converted or combined.</param>
public sealed record CostRollupHour(
    DateTimeOffset HourUtc,
    long CallCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long PricedCallCount,
    long UnpricedCallCount,
    long EstimatedUsageCallCount,
    long EstimatedUsageTotalTokens,
    IReadOnlyList<CostRollupSpend> SpendTotals);
