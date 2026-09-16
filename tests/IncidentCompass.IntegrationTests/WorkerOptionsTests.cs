using System.Text.Json.Nodes;
using IncidentCompass.Infrastructure.Notifications.Telegram;
using IncidentCompass.TestSupport;
using IncidentCompass.Worker;

namespace IncidentCompass.IntegrationTests;

public sealed class WorkerOptionsTests
{
    [Fact]
    public void Defaults_UseLongerLease()
    {
        var options = new WorkerOptions();

        Assert.Equal(900, options.LeaseSeconds);
    }

    /// <summary>
    /// The shipped attempt ceiling is hours long, far beyond a lease. That is safe because a Worker
    /// renews the lease of the job it is processing for as long as it holds it, so the lease is sized
    /// for how soon a crashed Worker's job becomes claimable again, not for how long an investigation
    /// may run. This pins the shipped lease values and that the shipped ceiling is not a short deadline.
    /// </summary>
    [Fact]
    public void ShippedLeases_AreRenewedRatherThanSizedToTheShippedAttemptCeiling()
    {
        var repositoryRoot = RepositoryRootLocator.Find();
        var developmentSettings = JsonNode.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "src", "IncidentCompass.Worker", "appsettings.Development.json")))!;
        var productionSettings = JsonNode.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "src", "IncidentCompass.Worker", "appsettings.json")))!;
        var budget = JsonNode.Parse(
            File.ReadAllText(Path.Combine(repositoryRoot, "config", "incidentcompass.config.json")))!["Orchestrator"]!["Budget"]!;

        var developmentLeaseSeconds = developmentSettings["IncidentCompass"]!["Worker"]!["LeaseSeconds"]!.GetValue<int>();
        var productionLeaseSeconds = productionSettings["IncidentCompass"]!["Worker"]!["LeaseSeconds"]!.GetValue<int>();
        var attemptCeilingSeconds =
            (budget["MaxAttemptDurationSeconds"] ?? budget["MaxWallClockSeconds"])?.GetValue<int>() ?? 14_400;

        Assert.Equal(900, productionLeaseSeconds);
        Assert.InRange(developmentLeaseSeconds, 1, productionLeaseSeconds);
        Assert.True(
            attemptCeilingSeconds is 0 or >= 600,
            $"The shipped attempt ceiling ({attemptCeilingSeconds}s) must not be a short investigation deadline.");
    }

    [Fact]
    public void Validator_RejectsNonPositivePollAndLeaseSeconds()
    {
        var validator = CreateValidator();
        var options = new WorkerOptions
        {
            MaxConcurrentJobs = 1,
            PollIntervalSeconds = 0,
            LeaseSeconds = 0,
            MaxAttempts = 1,
            RetryDelaySeconds = 0
        };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("PollIntervalSeconds", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("LeaseSeconds", StringComparison.Ordinal));
    }

    [Fact]
    public void ShippedTelegramBindingIsDisabledAndContainsNoCredential()
    {
        var repositoryRoot = RepositoryRootLocator.Find();
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "IncidentCompass.Worker", "appsettings.json")))!;
        var telegram = settings["IncidentCompass"]!["Telegram"]!;

        Assert.False(telegram["Enabled"]!.GetValue<bool>());
        Assert.Equal(string.Empty, telegram["RouteId"]!.GetValue<string>());
        Assert.Equal(string.Empty, telegram["ChatId"]!.GetValue<string>());
        Assert.Equal(string.Empty, telegram["BotToken"]!.GetValue<string>());
        Assert.True(new TelegramOptionsValidator().Validate(null, new TelegramOptions()).Succeeded);
    }

    // Resolving the concrete type directly instead of via Assembly.GetType("...") means a rename
    // is a compile error here rather than a runtime "type not found" failure.
    private static WorkerOptionsValidator CreateValidator() => new();
}
