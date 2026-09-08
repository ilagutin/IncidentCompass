using System.Text.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.OpenAiCompatible;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

internal sealed class OpenAiCompatibleModelClient(
    HttpClient httpClient,
    IOptions<OpenAiCompatibleModelClientOptions> options)
    : IAiModelClient
{
    private readonly OpenAiCompatibleRetryPolicy retryPolicy = new();

    public async Task<AiModelResponse> CompleteAsync(
        AiModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var clientOptions = GetClientOptions();
        var endpointUri = GetEndpointUri(clientOptions);
        var payloadJson = OpenAiModelRequestFactory.CreatePayloadJson(request);
        var maxRetryAttempts = Math.Max(0, clientOptions.MaxRetryAttempts);
        var idempotencyKey = CreateIdempotencyKey();

        for (var attempt = 0; ; attempt++)
        {
            using var httpRequest = OpenAiModelRequestFactory.CreateHttpRequest(
                clientOptions,
                request,
                payloadJson,
                endpointUri,
                idempotencyKey);
            try
            {
                using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptTimeout.CancelAfter(TimeSpan.FromSeconds(clientOptions.TimeoutSeconds));

                using var httpResponse = await httpClient.SendAsync(httpRequest, attemptTimeout.Token);
                var responseContent = await httpResponse.Content.ReadAsStringAsync(attemptTimeout.Token);

                if (!httpResponse.IsSuccessStatusCode)
                {
                    if (OpenAiCompatibleFailureClassifier.IsRetryableGenerationStatus(httpResponse.StatusCode) &&
                        attempt < maxRetryAttempts)
                    {
                        await DelayBeforeRetryAsync(
                            clientOptions,
                            httpResponse,
                            attempt,
                            cancellationToken);
                        continue;
                    }

                    throw OpenAiModelErrorMapper.FromHttpFailure(
                        httpResponse.StatusCode,
                        responseContent);
                }

                return OpenAiModelResponseMapper.Map(responseContent, request);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                throw OpenAiModelErrorMapper.Timeout(exception);
            }
            catch (HttpRequestException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (OpenAiCompatibleFailureClassifier.IsSafePreDispatchFailure(exception) &&
                    attempt < maxRetryAttempts)
                {
                    await DelayBeforeRetryAsync(
                        clientOptions,
                        response: null,
                        attempt,
                        cancellationToken);
                    continue;
                }

                throw OpenAiModelErrorMapper.Transport(exception);
            }
            catch (JsonException exception)
            {
                throw OpenAiModelErrorMapper.InvalidJson(exception);
            }
        }
    }

    private Task DelayBeforeRetryAsync(
        OpenAiCompatibleModelClientOptions clientOptions,
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

    private OpenAiCompatibleModelClientOptions GetClientOptions()
    {
        return OpenAiCompatibleOptionsResolver.Get(
            options,
            exception => new AiModelException(
                OpenAiModelProvider.Name,
                "OpenAI-compatible model provider configuration is invalid.",
                errorCode: "configuration_error",
                innerException: exception,
                failureKind: ProviderFailureKind.RejectedRequest));
    }

    private static Uri GetEndpointUri(OpenAiCompatibleModelClientOptions clientOptions)
    {
        return OpenAiCompatibleOptionsResolver.GetEndpointUri(
            clientOptions.IsValid(),
            clientOptions.TryCreateEndpointUri(out var endpointUri),
            endpointUri,
            () => new AiModelException(
                OpenAiModelProvider.Name,
                "OpenAI-compatible model provider configuration is invalid.",
                errorCode: "configuration_error",
                failureKind: ProviderFailureKind.RejectedRequest));
    }

    private static string CreateIdempotencyKey()
    {
        return $"incidentcompass-{Guid.NewGuid():N}";
    }
}
