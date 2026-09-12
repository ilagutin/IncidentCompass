using System.Net;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.OpenAiCompatible;

namespace IncidentCompass.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiEmbeddingRetryPolicy
{
    private readonly OpenAiCompatibleRetryPolicy retryPolicy = new();

    public bool ShouldRetry(HttpStatusCode statusCode)
    {
        return OpenAiCompatibleFailureClassifier.IsRetryableEmbeddingStatus(statusCode);
    }

    public Task DelayBeforeRetryAsync(
        OpenAiCompatibleEmbeddingClientOptions clientOptions,
        HttpResponseMessage? response,
        int attempt,
        CancellationToken cancellationToken)
    {
        return retryPolicy.DelayBeforeRetryAsync(
            clientOptions.RetryBaseDelayMilliseconds,
            clientOptions.MaxRetryDelaySeconds,
            response,
            attempt,
            cancellationToken);
    }
}
