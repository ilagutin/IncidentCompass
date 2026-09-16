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
    /// <summary>
    /// The largest chat response body read, streamed or not: 32 MiB, set on the registered typed client
    /// as its <see cref="HttpClient.MaxResponseContentBufferSize" />. A streamed answer spends a few
    /// hundred bytes of event framing per token, so even a 64000-token output streamed with its
    /// reasoning stays below it, while a hostile or runaway body is refused long before the framework default of
    /// about 2 GB.
    /// </summary>
    public const long MaxResponseContentBytes = 32 * 1024 * 1024;

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
            // the first-output limit runs from dispatch until output starts, which is the first data
            // event of a streamed answer and the response headers of any other; the inactivity limit
            // then restarts on every data event of a stream, or whenever another body delivers bytes.
            // Both timers use the injected clock.
            using var firstOutputTimeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(clientOptions.ResolveFirstOutputTimeoutSeconds()),
                timeProvider);
            using var inactivityTimeout = new CancellationTokenSource(Timeout.InfiniteTimeSpan, timeProvider);
            var responseStarted = false;
            var outputStarted = false;
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

                // The answer's shape, not the request, picks the parser: a provider that ignores
                // `stream` answers with one JSON body and is read exactly as before.
                if (httpResponse.IsSuccessStatusCode &&
                    OpenAiStreamingResponseReader.IsEventStream(httpResponse.Content))
                {
                    var streamedCompletion = await OpenAiStreamingResponseReader.ReadAsync(
                        httpResponse.Content,
                        firstOutputTimeout,
                        inactivityTimeout,
                        TimeSpan.FromSeconds(clientOptions.StreamInactivityTimeoutSeconds),
                        httpClient.MaxResponseContentBufferSize,
                        () => outputStarted = true,
                        cancellationToken);
                    return OpenAiModelResponseMapper.Map(streamedCompletion, request);
                }

                outputStarted = true;
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
                    outputStarted,
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
