using IncidentCompass.Application.Core.Errors;

namespace IncidentCompass.Application.Core.Resilience;

internal static class ProviderOutageExceptionClassifier
{
    public static bool IsProviderOutage(Exception exception) =>
        FindFailureKind(exception) == ProviderFailureKind.Unavailable;

    public static ProviderFailureKind? FindFailureKind(Exception exception)
    {
        var foundProvider = false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is not ProviderException providerException)
            {
                continue;
            }

            foundProvider = true;
            if (providerException.FailureKind != ProviderFailureKind.Unknown)
            {
                return providerException.FailureKind;
            }
        }

        return foundProvider ? ProviderFailureKind.Unknown : null;
    }

    public static string? FindSafeErrorCode(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProviderException { ErrorCode: { } errorCode } && IsSafeErrorCode(errorCode))
            {
                return errorCode;
            }
        }

        return null;
    }

    private static bool IsSafeErrorCode(string errorCode)
    {
        if (errorCode.Length is < 1 or > 80)
        {
            return false;
        }

        return errorCode.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }
}
