using IncidentCompass.Tester.Evaluation;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The Tester reports the attempt ceiling an evaluation ran under, resolved the way the backend
/// resolves it, so a configuration on either key, or on neither, still produces a result.
/// </summary>
public sealed class EvaluationConfigurationSnapshotLoaderTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "ic-evaluation-config-" + Guid.NewGuid().ToString("N") + ".json");

    [Theory]
    [InlineData("\"MaxAttemptDurationSeconds\": 7200,", 7200, "MaxAttemptDurationSeconds", null)]
    [InlineData("\"MaxAttemptDurationSeconds\": 0,", 0, "MaxAttemptDurationSeconds", null)]
    [InlineData("\"MaxWallClockSeconds\": 600,", 600, "MaxWallClockSeconds", 600)]
    [InlineData("", 14_400, "default", null)]
    public void Load_ReportsTheResolvedAttemptCeilingAndItsSource(string attemptDuration, int expectedSeconds, string expectedSetting, int? expectedWallClockSeconds)
    {
        File.WriteAllText(path, $$"""
            {
              "Routes": {
                "report-chat": { "Kind": "Chat", "ProviderId": "local-oai", "Model": "local-model" }
              },
              "Orchestrator": {
                "Budget": { "MaxWorkers": 6, "MaxTokens": 200000, {{attemptDuration}} "MaxReprompts": 2 }
              }
            }
            """);

        var budget = EvaluationConfigurationSnapshotLoader.Load(path).OrchestratorBudget;

        Assert.Equal(expectedSeconds, budget.MaxAttemptDurationSeconds);
        Assert.Equal(expectedSetting, budget.AttemptDurationSetting);
        Assert.Equal(expectedWallClockSeconds, budget.MaxWallClockSeconds);
        Assert.Equal(6, budget.MaxWorkers);
        Assert.Equal(2, budget.MaxReprompts);
    }

    public void Dispose()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
