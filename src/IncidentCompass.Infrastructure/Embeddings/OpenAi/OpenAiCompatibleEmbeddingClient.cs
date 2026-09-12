using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.OpenAiCompatible;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiCompatibleEmbeddingClient(
    HttpClient httpClient,
    IOptions<OpenAiCompatibleEmbeddingClientOptions> options,
    OpenAiCompatibleProviderProfileResolver providerProfileResolver)
    : IEmbeddingClient
{
    private readonly OpenAiEmbeddingExecutor executor = new(
        httpClient,
        new OpenAiEmbeddingOptionsResolver(options, providerProfileResolver),
        new OpenAiEmbeddingRequestFactory(),
        new OpenAiEmbeddingResponseMapper(),
        new OpenAiEmbeddingErrorMapper(),
        new OpenAiEmbeddingRetryPolicy());

    public async Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        return await executor.CreateEmbeddingAsync(
            request,
            cancellationToken);
    }
}
