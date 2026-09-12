using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.OpenAiCompatible;

namespace IncidentCompass.TestSupport;

/// <summary>
/// Builds the provider-selection collaborators the OpenAI-compatible adapters now take, without
/// standing up a host or writing a configuration file.
/// <para>
/// The stubs are private nested types on purpose. They exist only to satisfy two constructor
/// parameters, and a test reading <c>CreateResolver(...)</c> should not have to open two more files
/// to find out that the secret reader is a dictionary and the repository returns what it was given.
/// </para>
/// </summary>
internal static class TestModelProviderProfiles
{
    /// <summary>
    /// Creates a resolver over an in-memory provider table and secret map. Passing no provider
    /// table gives a resolver whose repository throws if it is ever read, which is what a test
    /// asserting the blank-provider path wants: the assertion is that no configuration was
    /// consulted, and a throwing stub states that rather than trusting it.
    /// </summary>
    public static OpenAiCompatibleProviderProfileResolver CreateResolver(
        IReadOnlyDictionary<string, TriageProviderSettings>? providers = null,
        IReadOnlyDictionary<string, string>? secrets = null)
    {
        return new OpenAiCompatibleProviderProfileResolver(
            new StubTriageConfigurationRepository(providers),
            new StubModelProviderSecretReader(secrets ?? new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    public static TriageConfiguration CreateConfiguration(
        IReadOnlyDictionary<string, TriageProviderSettings> providers)
    {
        return new TriageConfiguration(
            "test-config-hash",
            providers,
            new Dictionary<string, TriageRouteSettings>(StringComparer.Ordinal),
            new OrchestratorSettings(
                "Investigate and publish a report.",
                "report-chat",
                ["delegate", "publish_report"],
                new OrchestratorBudgetSettings(
                    MaxWorkers: 2,
                    MaxTokens: 100000,
                    MaxWallClockSeconds: 600,
                    MaxReprompts: 1)),
            new Dictionary<string, TriageRoleSettings>(StringComparer.Ordinal),
            new Dictionary<string, TriageToolSettings>(StringComparer.Ordinal),
            [],
            new IngestionSettings("local", ["tester"]),
            new FaultGroupingSettings(15, 30, 1, new MassIssueSettings(5, "strong")),
            RedactionSettings.Default);
    }

    private sealed class StubModelProviderSecretReader(IReadOnlyDictionary<string, string> secrets)
        : IModelProviderSecretReader
    {
        public string? Read(string secretRef) =>
            secrets.TryGetValue(secretRef, out var value) ? value : null;
    }

    private sealed class StubTriageConfigurationRepository(
        IReadOnlyDictionary<string, TriageProviderSettings>? providers)
        : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
            providers is null
                ? throw new InvalidOperationException(
                    "The triage configuration was read, but this test supplied no provider table.")
                : Task.FromResult(CreateConfiguration(providers));

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
            GetCurrentAsync(cancellationToken);
    }
}
