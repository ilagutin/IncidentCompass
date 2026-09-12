using IncidentCompass.TestSupport;

namespace IncidentCompass.IntegrationTests;

public sealed class ConfigurationSecretTests
{
    /// <summary>
    /// Every triage configuration the repository carries, discovered rather than listed. A hardcoded
    /// list silently stopped covering the evaluation configuration once it was added, so the
    /// credential-hygiene guarantee has to follow the same discovery the schema guards use.
    /// </summary>
    public static TheoryData<string> TriageConfigurationPaths
    {
        get
        {
            var configurationPaths = TriageConfigurationFileLocator.FindAll();
            TriageConfigurationFileLocator.AssertDiscoveryCoversEveryKnownConfiguration(
                configurationPaths);
            var data = new TheoryData<string>();
            foreach (var configurationPath in configurationPaths)
            {
                data.Add(configurationPath);
            }

            return data;
        }
    }

    [Theory]
    [InlineData("src/IncidentCompass.Api/appsettings.json")]
    [InlineData("src/IncidentCompass.Worker/appsettings.json")]
    public async Task RuntimeAppSettings_DoNotContainPostgresPasswords(string relativePath)
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(RepositoryRootLocator.Find(), relativePath));

        Assert.DoesNotContain("incidentcompass_dev_password", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(TriageConfigurationPaths))]
    public async Task TriageConfigurationContainsNoTelegramHostAuthorityOrCredential(string configurationPath)
    {
        var content = await File.ReadAllTextAsync(configurationPath);

        Assert.DoesNotContain("BotToken", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ChatId", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api.telegram.org", content, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(TriageConfigurationPaths))]
    public async Task TriageConfigurationContainsNoGitHubHostAuthorityOrCredential(string configurationPath)
    {
        var content = await File.ReadAllTextAsync(configurationPath);

        Assert.DoesNotContain("GitHub", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api.github.com", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner/repo", content, StringComparison.OrdinalIgnoreCase);
    }
}
