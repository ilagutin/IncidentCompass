using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Core.Errors;

namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

/// <summary>
/// The in-process embedding adapter the <c>LocalOnnx</c> provider kind selects. No model store and
/// no runtime are composed yet, so every call is refused with
/// <see cref="LocalOnnxEmbeddingProvider.ModelNotInstalledErrorCode" /> as an unavailable provider
/// rather than answered: a host configured for a local model that is not installed fails at its
/// first embedding call with a named code, and never receives a vector from anywhere else.
/// </summary>
internal sealed class LocalOnnxEmbeddingClient : IEmbeddingClient
{
    public Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<EmbeddingResponse>(cancellationToken);
        }

        return Task.FromException<EmbeddingResponse>(new EmbeddingClientException(
            LocalOnnxEmbeddingProvider.Name,
            "The local embedding model is not installed on this host.",
            errorCode: LocalOnnxEmbeddingProvider.ModelNotInstalledErrorCode,
            failureKind: ProviderFailureKind.Unavailable));
    }
}
