using System.Runtime.CompilerServices;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Runs a test that needs the pinned real relevance judge only when
/// <c>INCIDENTCOMPASS_REQUIRE_REAL_RELEVANCE_JUDGE</c> is set to a truthy value.
/// <para>
/// Unlike <see cref="RealEmbeddingModelFactAttribute" />, it deliberately does not run merely
/// because <c>CI</c> is set. The judge's ONNX file is about 544 MiB, and fetching that on every CI
/// run buys less than it costs: the pinned artifact's digest, licence and layout are covered by a
/// fresh-host check instead, and everything this adapter does with a model is covered
/// deterministically against the committed fixture. This attribute is how a maintainer runs the real
/// thing on purpose.
/// </para>
/// </summary>
public sealed class RealRelevanceJudgeModelFactAttribute : FactAttribute
{
    public const string EnabledVariable = "INCIDENTCOMPASS_REQUIRE_REAL_RELEVANCE_JUDGE";

    public RealRelevanceJudgeModelFactAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnabledVariable)))
        {
            Skip = "The real relevance judge test downloads about 544 MiB, so it runs only with " +
                   EnabledVariable + " set.";
        }
    }

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
