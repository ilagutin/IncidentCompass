namespace IncidentCompass.IntegrationTests;

/// <summary>
/// The attack, run once with the band decided against the fault query and once, for comparison, against
/// the model's own query. <c>ConfirmedAgainst</c> names which. Only the first is the product; the second
/// reproduces the rule the product replaced, so a zero in the first is shown to be the fix and not an
/// attack that confirms nothing under either rule.
/// </summary>
public sealed record MemorySearchRelevanceJudgeBenchmarkAttackLeg(
    string Name,
    string ConfirmedAgainst,
    IReadOnlyList<MemorySearchRelevanceJudgeBenchmarkAttackLanguage> Languages);
