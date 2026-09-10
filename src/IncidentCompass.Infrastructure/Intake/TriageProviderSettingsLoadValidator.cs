using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Infrastructure.Configuration;
using static IncidentCompass.Infrastructure.Intake.TriageConfigurationValidationGuards;

namespace IncidentCompass.Infrastructure.Intake;

/// <summary>
/// Validates the <c>Providers</c> table of a triage configuration at load, so a provider that could
/// never answer a call is rejected while the host is starting rather than at the first model call.
/// <para>
/// The rule that shapes this file is the single-provider default. The host-wide
/// <c>IncidentCompass:ModelGateway:OpenAiCompatible</c> and
/// <c>IncidentCompass:Embeddings:OpenAiCompatible</c> sections are the default provider profile, and
/// a configuration that declares exactly one provider keeps using them; that provider's
/// <c>Endpoint</c> and <c>ApiKeySecretRef</c> override the default where they resolve and the
/// default fills whatever they leave. That is what keeps every configuration shipped today working
/// with no environment change.
/// </para>
/// <para>
/// A configuration that declares more than one provider gets no default at all: every
/// <c>OpenAICompatible</c> entry must name its own <c>Endpoint</c> and its own
/// <c>ApiKeySecretRef</c>, and each named variable must be set. The line is drawn here rather than
/// per-entry because the failure a default would cause in a multi-provider configuration is not a
/// missing call, it is the first provider's credential arriving at the second provider's endpoint.
/// Refusing to start is the only safe answer to that.
/// </para>
/// </summary>
internal static class TriageProviderSettingsLoadValidator
{
    private const string OpenAiCompatibleKind = "OpenAICompatible";

    private static readonly HashSet<string> ProviderKinds =
        new(["Mock", OpenAiCompatibleKind], StringComparer.Ordinal);

    public static void Validate(
        IReadOnlyDictionary<string, TriageProviderSettings> providers,
        IModelProviderSecretReader secretReader)
    {
        var hostDefaultApplies = providers.Count <= 1;

        foreach (var (providerId, provider) in providers)
        {
            RequireKey(providerId, "Providers");
            RequireKnown("Providers." + providerId + ".Kind", provider.Kind, ProviderKinds);
            ValidateEndpoint(providerId, provider, hostDefaultApplies);
            ValidateApiKeySecretRef(providerId, provider, hostDefaultApplies, secretReader);
        }
    }

    private static void ValidateEndpoint(
        string providerId,
        TriageProviderSettings provider,
        bool hostDefaultApplies)
    {
        var settingName = "Providers." + providerId + ".Endpoint";

        if (string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            if (hostDefaultApplies || !IsOpenAiCompatible(provider))
            {
                return;
            }

            throw Invalid(
                settingName,
                string.Empty,
                "an absolute http or https endpoint, because a configuration with more than one" +
                " provider gets no host-wide default endpoint");
        }

        if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpointUri) ||
            (endpointUri.Scheme != Uri.UriSchemeHttps && endpointUri.Scheme != Uri.UriSchemeHttp))
        {
            throw Invalid(settingName, provider.Endpoint, "an absolute http or https endpoint");
        }
    }

    /// <summary>
    /// Checks that the named variable is set, never what it is set to. The setting name and the
    /// variable name reach the failure message; the value the reader returns is compared against
    /// blank and then discarded, so a bad configuration cannot print a credential into a startup
    /// log or a container's exit output.
    /// </summary>
    private static void ValidateApiKeySecretRef(
        string providerId,
        TriageProviderSettings provider,
        bool hostDefaultApplies,
        IModelProviderSecretReader secretReader)
    {
        var settingName = "Providers." + providerId + ".ApiKeySecretRef";

        if (string.IsNullOrWhiteSpace(provider.ApiKeySecretRef))
        {
            if (hostDefaultApplies || !IsOpenAiCompatible(provider))
            {
                return;
            }

            throw Invalid(
                settingName,
                string.Empty,
                "the name of an environment variable holding this provider's credential, because a" +
                " configuration with more than one provider gets no host-wide default credential");
        }

        if (hostDefaultApplies || !IsOpenAiCompatible(provider))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(secretReader.Read(provider.ApiKeySecretRef)))
        {
            throw Invalid(
                settingName,
                provider.ApiKeySecretRef,
                "the name of an environment variable that is set in this process");
        }
    }

    private static bool IsOpenAiCompatible(TriageProviderSettings provider) =>
        string.Equals(provider.Kind, OpenAiCompatibleKind, StringComparison.Ordinal);
}
