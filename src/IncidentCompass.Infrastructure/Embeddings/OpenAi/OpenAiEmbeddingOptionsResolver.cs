using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.OpenAiCompatible;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiEmbeddingOptionsResolver(
    IOptions<OpenAiCompatibleEmbeddingClientOptions> options)
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

    public Uri GetEndpointUri(OpenAiCompatibleEmbeddingClientOptions clientOptions)
    {
        return OpenAiCompatibleOptionsResolver.GetEndpointUri(
            clientOptions.IsValid(),
            clientOptions.TryCreateEndpointUri(out var endpointUri),
            endpointUri,
            () => new EmbeddingClientException(
                OpenAiEmbeddingProvider.Name,
                "OpenAI-compatible embedding provider configuration is invalid.",
                errorCode: "configuration_error",
                failureKind: ProviderFailureKind.RejectedRequest));
    }
}
