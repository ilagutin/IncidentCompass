using System.Text.Json.Nodes;
using IncidentCompass.Tester;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

public sealed class TesterOptionsTests
{
    [Fact]
    public void Defaults_ExceedShippedInvestigationWallClockBudget()
    {
        var repositoryRoot = RepositoryRootLocator.Find();
        var triageSettings = JsonNode.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "config", "incidentcompass.config.json")))!;
        var wallClockSeconds = triageSettings["Orchestrator"]!["Budget"]!["MaxWallClockSeconds"]!.GetValue<int>();
        var wallClockBudget = TimeSpan.FromSeconds(wallClockSeconds);
        var options = TesterOptions.Parse([], null, null, null, null, null);
        var shippedScenarioCount = DemoScenario.CreateAll("timeout-budget").Count + 1;

        Assert.InRange(options.PollTimeout.TotalSeconds, 660, 780);
        Assert.InRange(options.ScenarioTimeout.TotalSeconds, 720, 900);
        Assert.True(
            options.PollTimeout > wallClockBudget,
            $"Tester poll timeout ({options.PollTimeout.TotalSeconds}s) must exceed the shipped investigation budget ({wallClockSeconds}s).");
        Assert.True(
            options.ScenarioTimeout > wallClockBudget,
            $"Tester scenario timeout ({options.ScenarioTimeout.TotalSeconds}s) must exceed the shipped investigation budget ({wallClockSeconds}s).");
        Assert.True(
            options.TotalTimeout >= options.ScenarioTimeout * shippedScenarioCount + wallClockBudget,
            $"Tester total timeout ({options.TotalTimeout.TotalSeconds}s) must retain at least one investigation budget ({wallClockSeconds}s) of margin after {shippedScenarioCount} scenario ceilings.");
    }
}
