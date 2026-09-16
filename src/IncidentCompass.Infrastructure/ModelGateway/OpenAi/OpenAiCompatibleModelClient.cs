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
    IOptions<OpenAiCompatibleModelClientOptions> options,
    OpenAiCompatibleProviderProfileResolver providerProfileResolver,
    TimeProvider? timeProvider = null)
    : IAiModelClient
{
    private readonly OpenAiCompatibleRetryPolicy retryPolicy = new();
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AiModelResponse> CompleteAsync(
        AiModelRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var clientOptions = GetClientOptions();
        var providerProfile = await ResolveProviderProfileAsync(
            request.ProviderId,
            clientOptions,
            cancellationToken);
        var payloadJson = OpenAiModelRequestFactory.CreatePayloadJson(request, clientOptions);
        var maxRetryAttempts = Math.Max(0, clientOptions.MaxRetryAttempts);
        var idempotencyKey = CreateIdempotencyKey();

        for (var attempt = 0; ; attempt++)
        {
            using var httpRequest = OpenAiModelRequestFactory.CreateHttpRequest(
                clientOptions,
                request,
                payloadJson,
                providerProfile,
                idempotencyKey);

            // Each HTTP attempt has its own phases. The connect limit lives on the primary handler;
            // the first-output limit runs from dispatch until the response starts; the inactivity
            // limit restarts whenever the body delivers bytes. Both timers use the injected clock.
            using var firstOutputTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(clientOptions.ResolveFirstOutputTimeoutSeconds()),
                timeProvider);
            using var inactivityTimeout = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
            var responseStarted = false;
            try
            {
                using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    firstOutputTimeout.Token);
                using var httpResponse = await httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    sendCancellation.Token);
                responseStarted = true;
                var responseContent = await OpenAiResponseBodyReader.ReadAsync(
                    httpResponse.Content,
                    inactivityTimeout,
                    TimeSpan.FromSeconds(clientOptions.StreamInactivityTimeoutSeconds),
                    httpClient.MaxResponseContentBufferSize,
                    cancellationToken);

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
                throw OpenAiAttemptCancellationClassifier.Map(
                    exception,
                    responseStarted,
                    firstOutputTimeout.IsCancellationRequested,
                    inactivityTimeout.IsCancellationRequested,
                    httpClient.Timeout != Timeout.InfiniteTimeSpan);
            }
            catch (OpenAiResponseBodyTooLargeException exception)
            {
                throw OpenAiModelErrorMapper.ResponseTooLarge(exception);
            }
            catch (Exception exception) when (responseStarted && exception is IOException or HttpRequestException)
            {
                // The request was sent and the answer broke off mid-body. Nothing about that says the
                // provider did not act on it, so it is never replayed here, whatever the transport
                // classifier would say about the same exception before dispatch.
                cancellationToken.ThrowIfCancellationRequested();
                throw OpenAiModelErrorMapper.DispatchOutcomeUnknown(exception);
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

    /// <summary>
    /// Turns the request's route provider id into the endpoint and credential this call uses. The
    /// host-wide options remain the transport policy - path, timeout, retries, reasoning modes and
    /// the loopback allowance - and supply the endpoint and credential only when the provider table
    /// does not.
    /// </summary>
    private Task<OpenAiCompatibleProviderProfile> ResolveProviderProfileAsync(
        string? providerId,
        OpenAiCompatibleModelClientOptions clientOptions,
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
                clientOptions.ChatCompletionsPath,
                clientOptions.AllowInsecureHttpForLoopback),
            CreateInvalidConfigurationException,
            cancellationToken);
    }

    private static AiModelException CreateInvalidConfigurationException(string detail)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "OpenAI-compatible model provider configuration is invalid. " + detail,
            errorCode: "configuration_error",
            failureKind: ProviderFailureKind.RejectedRequest);
    }

    private static string CreateIdempotencyKey()
    {
        return $"incidentcompass-{Guid.NewGuid():N}";
    }
}
