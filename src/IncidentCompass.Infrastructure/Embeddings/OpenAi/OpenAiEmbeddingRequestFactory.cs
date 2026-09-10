using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Embeddings.OpenAi.Dtos;
using IncidentCompass.Infrastructure.OpenAiCompatible;

namespace IncidentCompass.Infrastructure.Embeddings.OpenAi;

internal sealed class OpenAiEmbeddingRequestFactory
{
    public string CreatePayloadJson(EmbeddingRequest request)
    {
        return JsonSerializer.Serialize(
            new OpenAiEmbeddingRequest(request.Model, request.Input),
            OpenAiEmbeddingJson.Options);
    }

    /// <summary>
    /// The endpoint and the credential both come from <paramref name="providerProfile" />, which the
    /// executor resolved from the request's route provider. <paramref name="clientOptions" /> still
    /// supplies the host-wide transport settings.
    /// </summary>
    public HttpRequestMessage CreateHttpRequest(
        OpenAiCompatibleEmbeddingClientOptions clientOptions,
        EmbeddingRequest request,
        string payloadJson,
        OpenAiCompatibleProviderProfile providerProfile)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, providerProfile.EndpointUri);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", providerProfile.ApiKey);

        if (!string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            httpRequest.Headers.Add("X-Correlation-Id", request.CorrelationId);
        }

        if (!string.IsNullOrWhiteSpace(clientOptions.Organization))
        {
            httpRequest.Headers.Add("OpenAI-Organization", clientOptions.Organization);
        }

        httpRequest.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        return httpRequest;
    }
}
