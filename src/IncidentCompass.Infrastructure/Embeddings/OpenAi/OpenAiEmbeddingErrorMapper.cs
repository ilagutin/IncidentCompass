using System.Net;
using System.Text.Json;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Infrastructure.OpenAiCompatible;

namespace IncidentCompass.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiEmbeddingErrorMapper
{
    public EmbeddingClientException FromHttpFailure(
        HttpStatusCode statusCode,
        string responseContent)
    {
        var providerError = OpenAiCompatibleErrorMapper.TryReadError(responseContent);
        return new EmbeddingClientException(
            OpenAiEmbeddingProvider.Name,
            $"Embedding provider returned HTTP {(int)statusCode}.",
            OpenAiCompatibleErrorMapper.NormalizeProviderErrorCode(statusCode),
            statusCode,
            providerError?.Error?.Code,
            failureKind: OpenAiCompatibleFailureClassifier.ClassifyEmbedding(statusCode));
    }

    public EmbeddingClientException EmptyEmbedding()
    {
        return new EmbeddingClientException(
            OpenAiEmbeddingProvider.Name,
            "Embedding provider returned no embedding vector.",
            errorCode: "empty_embedding",
            failureKind: ProviderFailureKind.InvalidResponse);
    }

    public EmbeddingClientException Timeout(OperationCanceledException exception)
    {
        return new EmbeddingClientException(
            OpenAiEmbeddingProvider.Name,
            "Embedding provider request timed out.",
            errorCode: "timeout",
            innerException: exception,
            failureKind: ProviderFailureKind.GenerationTimeout);
    }

    public EmbeddingClientException Transport(HttpRequestException exception)
    {
        var safePreDispatchFailure = OpenAiCompatibleFailureClassifier.IsSafePreDispatchFailure(exception);
        return new EmbeddingClientException(
            OpenAiEmbeddingProvider.Name,
            "Embedding provider request failed before a valid response was received.",
            errorCode: safePreDispatchFailure
                ? "provider_unavailable"
                : "transport_error",
            statusCode: exception.StatusCode,
            innerException: exception,
            failureKind: safePreDispatchFailure
                ? ProviderFailureKind.Unavailable
                : ProviderFailureKind.TransportFailure);
    }

    public EmbeddingClientException InvalidJson(JsonException exception)
    {
        return new EmbeddingClientException(
            OpenAiEmbeddingProvider.Name,
            "Embedding provider returned an invalid JSON response.",
            errorCode: "invalid_json",
            innerException: exception,
            failureKind: ProviderFailureKind.InvalidResponse);
    }
}
