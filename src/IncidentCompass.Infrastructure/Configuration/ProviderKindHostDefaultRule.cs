using IncidentCompass.Application.Intake.Configuration;

namespace IncidentCompass.Infrastructure.Configuration;

/// <summary>
/// The one rule that decides whether a triage configuration's <c>OpenAICompatible</c> entries may
/// fall back to the host-wide default provider profile. The load-time validator and the call-time
/// resolver both ask this type, so the rule that refuses a configuration at start and the rule that
/// resolves a call cannot drift apart.
/// <para>
/// The default applies when at most one counted provider is declared. A <c>LocalOnnx</c> entry is
/// not counted: it runs the model in-process, has no endpoint and no credential, and so cannot be
/// where one provider's credential arrives at another provider's endpoint, which is the failure the
/// count exists to prevent. A <c>Mock</c> entry is counted, exactly as it was before this kind
/// existed, so no configuration that loaded before changes behaviour.
/// </para>
/// </summary>
internal static class ProviderKindHostDefaultRule
{
    /// <summary>The <c>Providers[*].Kind</c> spelling of the in-process embedding provider.</summary>
    public const string LocalOnnxKind = "LocalOnnx";

    public static bool HostDefaultApplies(IReadOnlyDictionary<string, TriageProviderSettings> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var countedProviders = 0;
        foreach (var provider in providers.Values)
        {
            if (!string.Equals(provider.Kind, LocalOnnxKind, StringComparison.Ordinal))
            {
                countedProviders++;
            }
        }

        return countedProviders <= 1;
    }
}
