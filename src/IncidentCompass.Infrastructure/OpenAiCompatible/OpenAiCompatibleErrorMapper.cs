using System.Net;
using System.Text.Json;
using IncidentCompass.Application.Core.Errors;

namespace IncidentCompass.Infrastructure.OpenAiCompatible;

internal static class OpenAiCompatibleErrorMapper
{
    public static string NormalizeModelErrorCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => "invalid_request",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_error",
            HttpStatusCode.RequestTimeout => "provider_generation_timeout",
            HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => "provider_unavailable",
            HttpStatusCode.InternalServerError or
                HttpStatusCode.BadGateway or
                HttpStatusCode.GatewayTimeout => "provider_dispatch_outcome_unknown",
            _ when OpenAiCompatibleFailureClassifier.Classify(statusCode) ==
                ProviderFailureKind.RejectedRequest =>
                    "provider_request_rejected",
            _ => "provider_dispatch_outcome_unknown"
        };
    }

    public static string NormalizeProviderErrorCode(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest => "invalid_request",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "authentication_error",
            HttpStatusCode.RequestTimeout => "provider_timeout",
            HttpStatusCode.TooManyRequests => "rate_limited",
            HttpStatusCode.NotImplemented or HttpStatusCode.HttpVersionNotSupported =>
                "provider_request_rejected",
            _ when (int)statusCode >= 500 => "provider_unavailable",
            _ => "provider_request_rejected"
        };
    }

    public static OpenAiErrorResponse? TryReadError(string responseContent)
    {
        try
        {
            return JsonSerializer.Deserialize<OpenAiErrorResponse>(
                responseContent,
                OpenAiCompatibleJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
