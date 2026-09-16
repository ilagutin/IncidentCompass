using System.Text.Json.Nodes;
using IncidentCompass.Tester;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

public sealed class TesterOptionsTests
{
    /// <summary>
    /// The Tester's default waits are sized against the evaluation configuration, which keeps a
    /// finite ten-minute attempt ceiling on purpose. The shipped configuration's ceiling is hours
    /// long and is a safety net rather than an expected run length, so it is not what these defaults
    /// are measured against.
    /// </summary>
    [Fact]
    public void Defaults_ExceedEvaluationInvestigationAttemptCeiling()
    {
        var repositoryRoot = RepositoryRootLocator.Find();
        var budget = JsonNode.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "evaluations", "triage", "incidentcompass.config.json")))!["Orchestrator"]!["Budget"]!;
        var ceilingSeconds = (budget["MaxAttemptDurationSeconds"] ?? budget["MaxWallClockSeconds"])!.GetValue<int>();
        var ceiling = TimeSpan.FromSeconds(ceilingSeconds);
        var options = TesterOptions.Parse([], null, null, null, null, null);
        var shippedScenarioCount = DemoScenario.CreateAll("timeout-budget").Count + 1;

        Assert.InRange(ceilingSeconds, 1, 3600);
        Assert.InRange(options.PollTimeout.TotalSeconds, 660, 780);
        Assert.InRange(options.ScenarioTimeout.TotalSeconds, 720, 900);
        Assert.True(
            options.PollTimeout > ceiling,
            $"Tester poll timeout ({options.PollTimeout.TotalSeconds}s) must exceed the evaluation attempt ceiling ({ceilingSeconds}s).");
        Assert.True(
            options.ScenarioTimeout > ceiling,
            $"Tester scenario timeout ({options.ScenarioTimeout.TotalSeconds}s) must exceed the evaluation attempt ceiling ({ceilingSeconds}s).");
        Assert.True(
            options.TotalTimeout >= options.ScenarioTimeout * shippedScenarioCount + ceiling,
            $"Tester total timeout ({options.TotalTimeout.TotalSeconds}s) must retain at least one attempt ceiling ({ceilingSeconds}s) of margin after {shippedScenarioCount} scenario ceilings.");
    }
}
