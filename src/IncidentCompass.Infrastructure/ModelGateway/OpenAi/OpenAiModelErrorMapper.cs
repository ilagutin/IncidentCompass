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
    /// The provider accepted the request but its response did not start within the first-output
    /// limit. Without streaming that is one whole generation.
    /// </summary>
    public static AiModelException FirstOutputTimeout(OperationCanceledException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider response did not start within the first-output limit.",
            errorCode: ProviderErrorCodes.FirstOutputTimeout,
            innerException: exception,
            failureKind: ProviderFailureKind.GenerationTimeout);
    }

    /// <summary>
    /// The response started but its body then delivered nothing for the inactivity limit.
    /// </summary>
    public static AiModelException StreamInactivityTimeout(OperationCanceledException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider response stopped arriving for the inactivity limit.",
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
