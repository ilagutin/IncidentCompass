using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.OpenAiCompatible;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiEmbeddingOptionsResolver(
    IOptions<OpenAiCompatibleEmbeddingClientOptions> options,
    OpenAiCompatibleProviderProfileResolver providerProfileResolver)
{
    public OpenAiCompatibleEmbeddingClientOptions Get()
    {
        return OpenAiCompatibleOptionsResolver.Get(
            options,
            exception => new EmbeddingClientException(
                OpenAiEmbeddingProvider.Name,
                "OpenAI-compatible embedding provider configuration is invalid.",
                errorCode: "configuration_error",
                innerException: exception,
                failureKind: ProviderFailureKind.RejectedRequest));
    }

    /// <summary>
    /// Turns the request's route provider id into the endpoint and credential this embedding call
    /// uses. The embedding gateway follows the same provider table as the chat gateway on purpose:
    /// <c>memory-embed</c> is a route with a <c>ProviderId</c> like any other, and leaving
    /// embeddings pinned to the host-wide endpoint would mean a two-provider configuration silently
    /// sent its embeddings somewhere its routes never named.
    /// </summary>
    public Task<OpenAiCompatibleProviderProfile> ResolveProviderProfileAsync(
        string? providerId,
        OpenAiCompatibleEmbeddingClientOptions clientOptions,
        CancellationToken cancellationToken)
    {
        if (!clientOptions.IsTransportValid())
        {
            throw CreateInvalidConfigurationException(
                "The host-wide transport settings are outside their permitted ranges.");
        }

        return providerProfileResolver.ResolveAsync(
            providerId,
            new OpenAiCompatibleProviderDefaults(
                clientOptions.BaseUrl,
                clientOptions.ApiKey,
                clientOptions.EmbeddingsPath,
                clientOptions.AllowInsecureHttpForLoopback),
            CreateInvalidConfigurationException,
            cancellationToken);
    }

    private static EmbeddingClientException CreateInvalidConfigurationException(string detail)
    {
        return new EmbeddingClientException(
            OpenAiEmbeddingProvider.Name,
            "OpenAI-compatible embedding provider configuration is invalid. " + detail,
            errorCode: "configuration_error",
            failureKind: ProviderFailureKind.RejectedRequest);
    }
}
