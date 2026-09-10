using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Infrastructure.Configuration;

namespace IncidentCompass.Infrastructure.OpenAiCompatible;

/// <summary>
/// The single place where a route's provider id becomes an endpoint and a credential. Both
/// OpenAI-compatible adapters, chat and embedding, resolve through this type, so a two-provider
/// configuration cannot end up with chat on one provider and embeddings on another by accident.
/// <para>
/// Two sources feed one answer, and they are deliberately different sources. The endpoint comes
/// from the triage configuration's <c>Providers</c> table, which is tracked, reviewable and
/// snapshotted. The credential comes from <see cref="IModelProviderSecretReader" /> using the name
/// the entry's <c>ApiKeySecretRef</c> gives, so what is tracked is the name of a variable and never
/// its value.
/// </para>
/// <para>
/// The provider table is read from the currently loaded configuration rather than from the
/// snapshot a running job is pinned to. A job's route - its model, its ceilings, its reasoning
/// preference - stays pinned, because that is what makes a report reproducible. Where a call is
/// sent and what it authenticates with is operational, not behavioural, and an operator rotating a
/// key or moving an endpoint must not have to drain every in-flight job first.
/// </para>
/// <para>
/// A blank provider id resolves to the host-wide defaults without reading configuration at all.
/// That is the path a direct <c>IAiModelClient</c> caller outside the governed investigation takes,
/// and it is what keeps a host that has no triage configuration file able to make a model call.
/// </para>
/// <para>
/// Every failure here is a duplicate of a check <see cref="Intake.TriageProviderSettingsLoadValidator" />
/// already ran at load. That is intended: load-time validation is what makes a bad configuration
/// fail while the host starts, and these throws are what makes it fail closed rather than fall back
/// to some other provider's endpoint if a configuration ever reaches this point unvalidated.
/// </para>
/// </summary>
internal sealed class OpenAiCompatibleProviderProfileResolver(
    ITriageConfigurationRepository configurationRepository,
    IModelProviderSecretReader secretReader)
{
    private const string OpenAiCompatibleKind = "OpenAICompatible";

    /// <summary>
    /// Resolves one call's endpoint and credential from its route provider id.
    /// </summary>
    /// <param name="providerId">
    /// The route's configured provider identifier, or blank for the host-wide default profile.
    /// </param>
    /// <param name="defaults">The host-wide profile and the transport settings that stay host-wide.</param>
    /// <param name="createConfigurationException">
    /// Builds the adapter's own provider exception from a credential-free detail sentence. The
    /// caller supplies it so chat failures stay <c>AiModelException</c> and embedding failures stay
    /// <c>EmbeddingClientException</c>, with the error code and failure kind each adapter already
    /// reports for invalid configuration.
    /// </param>
    /// <param name="cancellationToken">Cancels the triage-configuration read.</param>
    public async Task<OpenAiCompatibleProviderProfile> ResolveAsync(
        string? providerId,
        OpenAiCompatibleProviderDefaults defaults,
        Func<string, Exception> createConfigurationException,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(createConfigurationException);

        if (string.IsNullOrWhiteSpace(providerId))
        {
            return CreateProfile(defaults.BaseUrl, defaults.ApiKey, defaults, createConfigurationException);
        }

        var configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
        if (!configuration.Providers.TryGetValue(providerId, out var provider))
        {
            throw createConfigurationException(
                $"Route provider '{providerId}' names no entry under Providers.");
        }

        if (!string.Equals(provider.Kind, OpenAiCompatibleKind, StringComparison.Ordinal))
        {
            throw createConfigurationException(
                $"Route provider '{providerId}' has Kind '{provider.Kind}', which this adapter cannot serve.");
        }

        var hostDefaultApplies = configuration.Providers.Count <= 1;
        return CreateProfile(
            ResolveBaseUrl(providerId, provider, defaults, hostDefaultApplies, createConfigurationException),
            ResolveApiKey(providerId, provider, defaults, hostDefaultApplies, createConfigurationException),
            defaults,
            createConfigurationException);
    }

    private static string ResolveBaseUrl(
        string providerId,
        TriageProviderSettings provider,
        OpenAiCompatibleProviderDefaults defaults,
        bool hostDefaultApplies,
        Func<string, Exception> createConfigurationException)
    {
        if (!string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            return provider.Endpoint;
        }

        if (hostDefaultApplies)
        {
            return defaults.BaseUrl;
        }

        throw createConfigurationException(
            $"Route provider '{providerId}' has no Endpoint, and a configuration with more than one" +
            " provider gets no host-wide default endpoint.");
    }

    private string ResolveApiKey(
        string providerId,
        TriageProviderSettings provider,
        OpenAiCompatibleProviderDefaults defaults,
        bool hostDefaultApplies,
        Func<string, Exception> createConfigurationException)
    {
        var resolved = string.IsNullOrWhiteSpace(provider.ApiKeySecretRef)
            ? null
            : secretReader.Read(provider.ApiKeySecretRef);

        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        if (hostDefaultApplies && !string.IsNullOrWhiteSpace(defaults.ApiKey))
        {
            return defaults.ApiKey;
        }

        // The secret ref is a variable name, which is safe to name and is the only thing an
        // operator can act on. The value it failed to produce is never mentioned, because there
        // isn't one, and a partially resolved one would still be a credential.
        throw createConfigurationException(
            string.IsNullOrWhiteSpace(provider.ApiKeySecretRef)
                ? $"Route provider '{providerId}' has no ApiKeySecretRef and no host-wide credential applies."
                : $"Route provider '{providerId}' names ApiKeySecretRef '{provider.ApiKeySecretRef}'," +
                  " which is not set in this process.");
    }

    private static OpenAiCompatibleProviderProfile CreateProfile(
        string baseUrl,
        string? apiKey,
        OpenAiCompatibleProviderDefaults defaults,
        Func<string, Exception> createConfigurationException)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw createConfigurationException("No provider credential is configured.");
        }

        if (!OpenAiCompatibleEndpointPolicy.TryCreateEndpointUri(
                baseUrl,
                defaults.EndpointPath,
                defaults.AllowInsecureHttpForLoopback,
                out var endpointUri) ||
            endpointUri is null)
        {
            throw createConfigurationException(
                "The resolved provider endpoint is not an allowed absolute URL. Insecure HTTP is" +
                " permitted only for loopback development configuration.");
        }

        return new OpenAiCompatibleProviderProfile(endpointUri, apiKey);
    }
}
