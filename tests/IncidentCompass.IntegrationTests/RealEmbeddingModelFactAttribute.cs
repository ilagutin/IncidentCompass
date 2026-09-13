using System.Runtime.CompilerServices;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Runs a test that needs the pinned real embedding model only where the Docker-backed test tier is
/// required: in CI, or with <c>INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS</c> set. The model is downloaded
/// through the model store when the cache directory is empty, so a developer machine that merely has
/// Docker running does not fetch about 120 MB on an ordinary test run.
/// </summary>
public sealed class RealEmbeddingModelFactAttribute : FactAttribute
{
    public RealEmbeddingModelFactAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable("CI")) &&
            !IsTruthy(Environment.GetEnvironmentVariable("INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS")))
        {
            Skip = "The real embedding model test runs where the Docker-backed test tier is required" +
                   " (CI or INCIDENTCOMPASS_REQUIRE_DOCKER_TESTS).";
        }
    }

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
