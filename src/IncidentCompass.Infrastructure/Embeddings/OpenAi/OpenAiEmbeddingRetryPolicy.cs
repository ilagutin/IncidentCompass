using System.Net;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.OpenAiCompatible;

namespace IncidentCompass.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiEmbeddingRetryPolicy
{
    private readonly OpenAiCompatibleRetryPolicy retryPolicy = new();

    public bool ShouldRetry(HttpStatusCode statusCode)
    {
        return retryPolicy.ShouldRetryEmbedding(statusCode);
    }

    public Task DelayBeforeRetryAsync(
        OpenAiCompatibleEmbeddingClientOptions clientOptions,
        HttpResponseMessage? response,
        int attempt,
        CancellationToken cancellationToken)
    {
        return retryPolicy.DelayBeforeRetryAsync(
            clientOptions.RetryBaseDelayMilliseconds,
            response,
            attempt,
            cancellationToken);
    }
}
