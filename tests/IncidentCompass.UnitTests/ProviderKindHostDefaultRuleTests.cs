using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.OpenAiCompatible;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The single-provider default is decided by one counting rule that the load validator and the
/// call-time resolver share. <c>LocalOnnx</c> entries are not counted, so adding the local
/// embedding provider beside the shipped chat provider leaves that provider on the host-wide
/// endpoint and credential; <c>Mock</c> entries still count, exactly as before.
/// </summary>
public sealed class ProviderKindHostDefaultRuleTests
{
    private const string HostWideBaseUrl = "https://host-wide.example";
    private const string HostWideApiKey = "sk-host-wide-key";

    [Fact]
    public void HostDefaultApplies_CountsEveryKindExceptLocalOnnx()
    {
        Assert.True(ProviderKindHostDefaultRule.HostDefaultApplies(Providers()));
        Assert.True(ProviderKindHostDefaultRule.HostDefaultApplies(Providers(("local-embed", "LocalOnnx"))));
        Assert.True(ProviderKindHostDefaultRule.HostDefaultApplies(
            Providers(("local-oai", "OpenAICompatible"), ("local-embed", "LocalOnnx"))));
        Assert.True(ProviderKindHostDefaultRule.HostDefaultApplies(
            Providers(("local-oai", "OpenAICompatible"), ("local-embed", "LocalOnnx"), ("other-embed", "LocalOnnx"))));
        Assert.False(ProviderKindHostDefaultRule.HostDefaultApplies(
            Providers(("local-oai", "OpenAICompatible"), ("remote-oai", "OpenAICompatible"))));
        Assert.False(ProviderKindHostDefaultRule.HostDefaultApplies(
            Providers(("local-oai", "OpenAICompatible"), ("mock", "Mock"))));
    }

    [Fact]
    public async Task Resolver_OneOpenAiCompatibleAndOneLocalOnnxEntry_KeepsTheHostDefaultEndpointAndCredential()
    {
        var resolver = TestModelProviderProfiles.CreateResolver(
            Providers(("local-oai", "OpenAICompatible"), ("local-embed", "LocalOnnx")));

        var profile = await resolver.ResolveAsync(
            "local-oai",
            HostDefaults(),
            static detail => new InvalidOperationException(detail),
            TestContext.Current.CancellationToken);

        Assert.Equal("host-wide.example", profile.EndpointUri.Host);
        Assert.Equal(HostWideApiKey, profile.ApiKey);
    }

    [Fact]
    public async Task Resolver_TwoOpenAiCompatibleEntries_StillGetNoHostDefault()
    {
        var resolver = TestModelProviderProfiles.CreateResolver(
            Providers(("local-oai", "OpenAICompatible"), ("remote-oai", "OpenAICompatible")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(
                "local-oai",
                HostDefaults(),
                static detail => new InvalidOperationException(detail),
                TestContext.Current.CancellationToken));

        Assert.Contains("more than one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_MockAndOpenAiCompatibleEntries_StillGetNoHostDefault()
    {
        var resolver = TestModelProviderProfiles.CreateResolver(
            Providers(("local-oai", "OpenAICompatible"), ("mock", "Mock")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync(
                "local-oai",
                HostDefaults(),
                static detail => new InvalidOperationException(detail),
                TestContext.Current.CancellationToken));

        Assert.Contains("more than one", exception.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, TriageProviderSettings> Providers(params (string Id, string Kind)[] entries)
    {
        var providers = new Dictionary<string, TriageProviderSettings>(StringComparer.Ordinal);
        foreach (var (id, kind) in entries)
        {
            providers[id] = new TriageProviderSettings(kind, Endpoint: null, ApiKeySecretRef: null);
        }

        return providers;
    }

    private static OpenAiCompatibleProviderDefaults HostDefaults() =>
        new(HostWideBaseUrl, HostWideApiKey, "/v1/embeddings", allowInsecureHttpForLoopback: false);
}
