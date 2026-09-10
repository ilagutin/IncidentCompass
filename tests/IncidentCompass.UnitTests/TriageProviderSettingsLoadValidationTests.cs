using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Intake;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// A provider entry that could never answer a call must be rejected while the configuration loads,
/// which is what the host does at startup through <c>TriageConfigurationWarmupHostedService</c>, and
/// not at the first model call in a Worker an hour later.
/// <para>
/// The credential-hygiene assertions in this file are as important as the fail-closed ones. A
/// startup failure is the single most likely place for a credential to escape, because the message
/// is written by the code that just finished looking the credential up and it lands in a container's
/// exit output where anyone with the logs can read it.
/// </para>
/// </summary>
public sealed class TriageProviderSettingsLoadValidationTests
{
    private const string SecretRef = "SECOND_PROVIDER_API_KEY";
    private const string SecretValue = "sk-second-provider-not-in-any-message";

    private static readonly JsonSerializerOptions ProviderJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The configurations shipped in this repository declare one provider each, so the host-wide
    /// OpenAI-compatible section stays their profile and nothing new is required of an operator.
    /// This runs with a secret reader that resolves nothing, which is the state of a checkout where
    /// only <c>appsettings.json</c> supplies a credential.
    /// </summary>
    [Theory]
    [MemberData(nameof(TriageConfigurationPaths))]
    public async Task ShippedConfigurations_LoadWithNoProviderSecretsInTheEnvironment(string configurationPath)
    {
        var providers = await ReadProvidersAsync(configurationPath);

        TriageProviderSettingsLoadValidator.Validate(providers, new EmptySecretReader());

        Assert.NotEmpty(providers);
    }

    [Fact]
    public void SingleProvider_WithNoEndpointOrSecretRef_Loads()
    {
        var providers = new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal)
        {
            ["local-oai"] = new("OpenAICompatible", Endpoint: null, ApiKeySecretRef: null)
        };

        TriageProviderSettingsLoadValidator.Validate(providers, new EmptySecretReader());
    }

    [Fact]
    public void SingleProvider_WithUnresolvableSecretRef_StillLoads()
    {
        var providers = new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal)
        {
            ["local-oai"] = new("OpenAICompatible", "https://provider-a.example", SecretRef)
        };

        TriageProviderSettingsLoadValidator.Validate(providers, new EmptySecretReader());
    }

    [Fact]
    public void Provider_WithNonAbsoluteEndpoint_FailsAtLoad()
    {
        var providers = new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal)
        {
            ["local-oai"] = new("OpenAICompatible", "not-a-valid-uri", ApiKeySecretRef: null)
        };

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            TriageProviderSettingsLoadValidator.Validate(providers, new EmptySecretReader()));

        Assert.Contains("Providers.local-oai.Endpoint", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleProviders_WithMissingEndpoint_FailAtLoad()
    {
        var providers = TwoProviders(secondEndpoint: null, secondSecretRef: SecretRef);

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            TriageProviderSettingsLoadValidator.Validate(providers, ReaderWithSecret()));

        Assert.Contains("Providers.remote-oai.Endpoint", exception.Message, StringComparison.Ordinal);
        Assert.Contains("more than one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleProviders_WithMissingSecretRef_FailAtLoad()
    {
        var providers = TwoProviders("https://provider-b.example", secondSecretRef: null);

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            TriageProviderSettingsLoadValidator.Validate(providers, ReaderWithSecret()));

        Assert.Contains("Providers.remote-oai.ApiKeySecretRef", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure names the environment variable, which is the only thing an operator can act on,
    /// and never a value. There is no value to print in this case, but the assertion also covers the
    /// first provider's credential, which the reader could resolve and which a careless "here is
    /// what we do have" message would have leaked into the startup failure.
    /// </summary>
    [Fact]
    public void MultipleProviders_WithUnresolvableSecretRef_FailAtLoadWithoutNamingACredential()
    {
        var providers = TwoProviders("https://provider-b.example", SecretRef);
        var firstProviderOnly = new DictionarySecretReader(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FIRST_PROVIDER_API_KEY"] = "first-provider-key"
        });

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            TriageProviderSettingsLoadValidator.Validate(providers, firstProviderOnly));

        Assert.Contains("Providers.remote-oai.ApiKeySecretRef", exception.Message, StringComparison.Ordinal);
        Assert.Contains(SecretRef, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("first-provider-key", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleProviders_WithEndpointAndResolvableSecretRef_Load()
    {
        var providers = TwoProviders("https://provider-b.example", SecretRef);

        TriageProviderSettingsLoadValidator.Validate(providers, ReaderWithSecret());
    }

    /// <summary>
    /// A Mock provider entry is exempt from the endpoint and credential requirements even in a
    /// multi-provider configuration, because there is no endpoint for it to reach and no credential
    /// for it to present.
    /// </summary>
    [Fact]
    public void MultipleProviders_DoNotRequireAnEndpointForAMockEntry()
    {
        var providers = new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal)
        {
            ["local-oai"] = new("OpenAICompatible", "https://provider-a.example", "FIRST_PROVIDER_API_KEY"),
            ["mock"] = new("Mock", Endpoint: null, ApiKeySecretRef: null)
        };

        TriageProviderSettingsLoadValidator.Validate(
            providers,
            ReaderWithSecret("FIRST_PROVIDER_API_KEY", "first-provider-key"));
    }

    /// <summary>
    /// The route-level check that a provider id names a real entry already existed; this pins it
    /// against the full load path a host runs at startup, so making <c>Providers</c> real cannot
    /// quietly weaken it.
    /// </summary>
    [Fact]
    public void Route_NamingAnUnknownProvider_FailsAtLoad()
    {
        var configNode = ConfigNodeWithRouteProvider("does-not-exist");

        var exception = Assert.Throws<TriageConfigurationLoadException>(() =>
            CreateMaterializer().Materialize("config-hash", configNode, ResolvedReferences()));

        Assert.Contains("Routes.analysis-chat.ProviderId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("does-not-exist", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretValue, exception.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string> TriageConfigurationPaths
    {
        get
        {
            var configurationPaths = TriageConfigurationFileLocator.FindAll();
            TriageConfigurationFileLocator.AssertDiscoveryCoversEveryKnownConfiguration(configurationPaths);
            var data = new TheoryData<string>();
            foreach (var configurationPath in configurationPaths)
            {
                data.Add(configurationPath);
            }

            return data;
        }
    }

    /// <summary>
    /// Reads only the <c>Providers</c> object, so this stays independent of whether the rest of a
    /// configuration file needs referenced instruction files to load. Environment placeholders are
    /// expanded first, using the same expander the file loader runs, because a shipped
    /// <c>Endpoint</c> is written as <c>${VAR:-fallback}</c> and the validator sees it expanded.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, TriageProviderSettings>> ReadProvidersAsync(
        string configurationPath)
    {
        var node = JsonNode.Parse(await File.ReadAllTextAsync(configurationPath, TestContext.Current.CancellationToken))
            ?? throw new InvalidOperationException(configurationPath + " is empty.");
        EnvironmentPlaceholderExpander.Expand(node);
        var providersNode = node["Providers"]?.ToJsonString()
            ?? throw new InvalidOperationException(configurationPath + " declares no Providers section.");

        return JsonSerializer.Deserialize<Dictionary<string, TriageProviderSettings>>(
                providersNode,
                ProviderJsonOptions)
            ?? throw new InvalidOperationException(configurationPath + " has an unreadable Providers section.");
    }

    private static Dictionary<string, TriageProviderSettings> TwoProviders(
        string? secondEndpoint,
        string? secondSecretRef)
    {
        return new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal)
        {
            ["local-oai"] = new("OpenAICompatible", "https://provider-a.example", "FIRST_PROVIDER_API_KEY"),
            ["remote-oai"] = new("OpenAICompatible", secondEndpoint, secondSecretRef)
        };
    }

    private static DictionarySecretReader ReaderWithSecret(
        string secretRef = SecretRef,
        string secretValue = SecretValue)
    {
        return new DictionarySecretReader(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [secretRef] = secretValue,
            ["FIRST_PROVIDER_API_KEY"] = "first-provider-key"
        });
    }

    private static TriageConfigurationMaterializer CreateMaterializer()
    {
        var registry = new SignalNormalizerRegistry([
            new TesterSignalNormalizer(),
            new OtelShapedSignalNormalizer(),
            new UserReportSignalNormalizer()
        ]);
        var tools = new AgentToolRegistry([
            new AgentToolDescriptor("memory_search", AgentToolCapability.ImmediateRead),
            TicketCreateTool.Descriptor
        ]);
        return new TriageConfigurationMaterializer(
            new TriageConfigurationLoadValidator(registry, tools, new EmptySecretReader()));
    }

    private static JsonNode ConfigNodeWithRouteProvider(string routeProviderId)
    {
        return JsonNode.Parse(
            $$"""
            {
              "Providers": {
                "local-oai": { "Kind": "OpenAICompatible", "Endpoint": "https://provider-a.example", "ApiKeySecretRef": "FIRST_PROVIDER_API_KEY" }
              },
              "Routes": {
                "analysis-chat": { "Kind": "Chat", "ProviderId": "{{routeProviderId}}", "Model": "local-model", "Temperature": 0.1, "MaxOutputTokens": 2000, "ContextWindowTokens": 8192 }
              },
              "Orchestrator": {
                "Instructions": "ref:instructions/orchestrator.md",
                "RouteId": "analysis-chat",
                "Tools": ["delegate", "publish_report"],
                "Budget": { "MaxWorkers": 2, "MaxTokens": 100000, "MaxWallClockSeconds": 600, "MaxReprompts": 1 }
              },
              "Roles": {
                "analysis": { "RouteId": "analysis-chat", "Instructions": "ref:instructions/analysis.md", "Tools": [], "OutputSchema": "ref:schemas/analysis.json" }
              },
              "Tools": {},
              "Rules": [],
              "Ingestion": { "DefaultTenant": "local", "AllowedSources": ["tester"] },
              "Redaction": { "AttributeKeys": [], "Patterns": [], "UserIdentifierAttributes": [] },
              "FaultGrouping": {
                "LookbackMinutes": 15,
                "SilenceWindowMinutes": 30,
                "FingerprintVersion": 1,
                "FingerprintRules": [],
                "SuppressionRules": [],
                "Recurrence": { "EscalateAfterCount": 3 },
                "MassIssue": { "MinNeighborCount": 5, "MinFingerprintStrength": "strong" }
              },
              "CurrentReleases": {}
            }
            """)!;
    }

    private static JsonObject ResolvedReferences() => new()
    {
        ["ref:instructions/orchestrator.md"] = "orchestrator body",
        ["ref:instructions/analysis.md"] = "analysis body",
        ["ref:schemas/analysis.json"] = "{ \"type\": \"object\" }"
    };

    private sealed class EmptySecretReader : IModelProviderSecretReader
    {
        public string? Read(string secretRef) => null;
    }

    private sealed class DictionarySecretReader(IReadOnlyDictionary<string, string> secrets)
        : IModelProviderSecretReader
    {
        public string? Read(string secretRef) =>
            secrets.TryGetValue(secretRef, out var value) ? value : null;
    }
}
