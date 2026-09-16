using System.Net;
using System.Text.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Infrastructure.OpenAiCompatible;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

internal static class OpenAiModelErrorMapper
{
    public const string ResponseTooLargeCode = "provider_response_too_large";

    public static AiModelException FromHttpFailure(
        HttpStatusCode statusCode,
        string responseContent)
    {
        var providerError = OpenAiCompatibleErrorMapper.TryReadError(responseContent);
        return new AiModelException(
            OpenAiModelProvider.Name,
            $"Model provider returned HTTP {(int)statusCode}.",
            OpenAiCompatibleErrorMapper.NormalizeModelErrorCode(statusCode),
            providerError?.Error?.Code,
            failureKind: OpenAiCompatibleFailureClassifier.Classify(statusCode));
    }

    /// <summary>
    /// The request was sent and the attempt was then interrupted before a complete answer arrived:
    /// the response body failed mid-read, or a cancellation that neither the caller nor one of the
    /// adapter limits accounts for ended it. Whether the provider acted on the request is unknown, so
    /// the call is never replayed inside the adapter.
    /// </summary>
    public static AiModelException DispatchOutcomeUnknown(Exception exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider request was interrupted after dispatch.",
            errorCode: "provider_dispatch_outcome_unknown",
            innerException: exception,
            failureKind: ProviderFailureKind.AmbiguousInterruption);
    }

    /// <summary>
    /// A streamed answer ended before it was complete: the body closed without the <c>[DONE]</c>
    /// marker and without any finish reason. What was received is not an answer, and whether the
    /// provider finished generating is unknown, so it is a dispatch whose outcome is unknown.
    /// </summary>
    public static AiModelException StreamTruncated()
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider stream ended before the completion finished.",
            errorCode: "provider_dispatch_outcome_unknown",
            failureKind: ProviderFailureKind.AmbiguousInterruption);
    }

    /// <summary>
    /// A streamed answer carried an <c>error</c> event after it had started. The request was sent and
    /// may have been partly generated and billed, so it is a failure after dispatch and is never
    /// replayed. Only the provider's error code is kept, as for an HTTP failure; its message is not.
    /// </summary>
    public static AiModelException StreamError(
        JsonElement error,
        AiModelUsage? usage,
        string? returnedModel)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider reported an error in its stream.",
            errorCode: "provider_dispatch_outcome_unknown",
            providerErrorCode: ReadProviderErrorCode(error),
            failureKind: ProviderFailureKind.AmbiguousInterruption,
            usage: usage,
            returnedModel: returnedModel);
    }

    private static string? ReadProviderErrorCode(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object || !error.TryGetProperty("code", out var code))
        {
            return null;
        }

        return code.ValueKind switch
        {
            JsonValueKind.String => code.GetString(),
            JsonValueKind.Number => code.GetRawText(),
            _ => null
        };
    }

    /// <summary>
    /// The response body grew past the HTTP client's maximum response content size and was not
    /// read further.
    /// </summary>
    public static AiModelException ResponseTooLarge(OpenAiResponseBodyTooLargeException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider response exceeded the maximum response size.",
            errorCode: ResponseTooLargeCode,
            innerException: exception,
            failureKind: ProviderFailureKind.InvalidResponse);
    }

    /// <summary>
    /// The connection to the provider could not be established within the connect limit. Nothing
    /// was generated; the failure kind and retry behaviour stay those of a timed-out call.
    /// </summary>
    public static AiModelException ConnectTimeout(OperationCanceledException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider connection was not established within the connect limit.",
            errorCode: ProviderErrorCodes.ConnectTimeout,
            innerException: exception,
            failureKind: ProviderFailureKind.GenerationTimeout);
    }

    /// <summary>
    /// The provider accepted the request but produced no output within the first-output limit: no
    /// response headers for a non-streamed answer, one whole generation, or no data event on a stream.
    /// </summary>
    public static AiModelException FirstOutputTimeout(OperationCanceledException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider produced no first output within the first-output limit.",
            errorCode: ProviderErrorCodes.FirstOutputTimeout,
            innerException: exception,
            failureKind: ProviderFailureKind.GenerationTimeout);
    }

    /// <summary>
    /// Output started and then stopped for the inactivity limit: no bytes of a non-streamed body, or
    /// no data event on a stream.
    /// </summary>
    public static AiModelException StreamInactivityTimeout(OperationCanceledException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider produced no further output within the inactivity limit.",
            errorCode: ProviderErrorCodes.StreamInactivityTimeout,
            innerException: exception,
            failureKind: ProviderFailureKind.GenerationTimeout);
    }

    public static AiModelException Transport(HttpRequestException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider request failed before a valid response was received.",
            errorCode: OpenAiCompatibleFailureClassifier.IsSafePreDispatchFailure(exception)
                ? "provider_unavailable"
                : "provider_dispatch_outcome_unknown",
            innerException: exception,
            failureKind: OpenAiCompatibleFailureClassifier.Classify(exception));
    }

    public static AiModelException InvalidJson(JsonException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider returned an invalid JSON response.",
            errorCode: "invalid_json",
            innerException: exception,
            failureKind: ProviderFailureKind.InvalidResponse);
    }

    public static AiModelException EmptyResponse(
        AiModelUsage? usage,
        string? returnedModel)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider returned no chat completion content.",
            errorCode: "empty_response",
            failureKind: ProviderFailureKind.InvalidResponse,
            usage: usage,
            returnedModel: returnedModel);
    }

    public static AiModelException OutputLimitReached(
        AiModelUsage? usage,
        string? returnedModel)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider reached its output limit before returning usable content.",
            errorCode: "provider_output_limit_reached",
            failureKind: ProviderFailureKind.OutputLimitReached,
            usage: usage,
            returnedModel: returnedModel);
    }

    public static AiModelException InvalidToolCall(
        AiModelUsage? usage,
        string? returnedModel,
        JsonException? innerException = null)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider returned a malformed tool call.",
            errorCode: "invalid_response",
            innerException: innerException,
            failureKind: ProviderFailureKind.InvalidResponse,
            usage: usage,
            returnedModel: returnedModel);
    }
}
