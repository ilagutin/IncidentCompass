using System.Text.RegularExpressions;
using IncidentCompass.Application.Memory;

namespace IncidentCompass.Infrastructure.Relevance.Mock;

/// <summary>
/// The mock stack's relevance judge. It is not a governance boundary and makes no claim about
/// relevance: it exists so a stack whose chat model and embeddings are both mocked runs the same
/// judged <c>memory_search</c> path the product runs, admission and confirmation included, instead of
/// a judge-less path on which nothing can ever be confirmed.
/// </summary>
/// <remarks>
/// <para>
/// The rule is deterministic and deliberately coarse, and it reads identifiers only:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A candidate that contains an error type the query names, such as <c>TimeoutException</c>, scores
/// <see cref="Confirmed" />, above the default confirm score.
/// </description></item>
/// <item><description>
/// Otherwise a candidate that contains a hyphenated service name the query names, such as
/// <c>payments-api</c>, scores <see cref="Related" />: above the default floor and below the default
/// confirm score, so it is returned as related context and never confirmed.
/// </description></item>
/// <item><description>Anything else scores <see cref="Unrelated" />, below the default floor, and is dropped.</description></item>
/// </list>
/// <para>
/// Those sides hold for the default thresholds only. The thresholds are configuration, and a mock
/// stack that set its own could put any of the three scores on the other side of either.
/// </para>
/// <para>
/// Under it the shipped demo timeout signal is confirmed against the shipped timeout runbook and known
/// incident, which both name <c>TimeoutException</c>, and the demo's null-reference signal is not,
/// because no shipped document names its error type.
/// </para>
/// </remarks>
internal sealed partial class MockMemoryRelevanceJudge : IMemoryRelevanceJudge
{
    /// <summary>Well above the default confirm score of 0.85.</summary>
    public const float Confirmed = 4.0f;

    /// <summary>Between the default floor of -0.25 and the default confirm score.</summary>
    public const float Related = 0.0f;

    /// <summary>Well below the default floor.</summary>
    public const float Unrelated = -4.0f;

    public Task<IReadOnlyList<float>> ScoreAsync(
        string query,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);
        cancellationToken.ThrowIfCancellationRequested();

        var errorTypes = ErrorTypePattern().Matches(query).Select(static match => match.Value).Distinct().ToArray();
        var serviceNames = ServiceNamePattern().Matches(query).Select(static match => match.Value).Distinct().ToArray();
        return Task.FromResult<IReadOnlyList<float>>(candidates
            .Select(candidate => Score(candidate, errorTypes, serviceNames))
            .ToArray());
    }

    private static float Score(string candidate, string[] errorTypes, string[] serviceNames)
    {
        if (errorTypes.Any(errorType => candidate.Contains(errorType, StringComparison.Ordinal)))
        {
            return Confirmed;
        }

        return serviceNames.Any(serviceName => candidate.Contains(serviceName, StringComparison.OrdinalIgnoreCase))
            ? Related
            : Unrelated;
    }

    /// <summary>A type name ending in <c>Exception</c> or <c>Error</c>, such as <c>TimeoutException</c>.</summary>
    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9]*(?:Exception|Error)\b", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorTypePattern();

    /// <summary>A lower-case hyphenated identifier, such as <c>payments-api</c>.</summary>
    [GeneratedRegex(@"\b[a-z0-9]+(?:-[a-z0-9]+)+\b", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNamePattern();
}
