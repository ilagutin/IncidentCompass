using System.Net;
using System.Text.Json;
using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Infrastructure.OpenAiCompatible;

namespace IncidentCompass.Infrastructure.ModelGateway.OpenAi;

internal static class OpenAiModelErrorMapper
{
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

    public static AiModelException Timeout(OperationCanceledException exception)
    {
        return new AiModelException(
            OpenAiModelProvider.Name,
            "Model provider request timed out.",
            errorCode: "provider_generation_timeout",
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
