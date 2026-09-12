using System.Globalization;

namespace IncidentCompass.Tester.Evaluation;

internal static class EvaluationFailureDetail
{
    public static string FromException(Exception exception)
    {
        if (exception is HttpRequestException httpException)
        {
            return httpException.StatusCode.HasValue
                ? "HttpRequestException: HTTP status " + ((int)httpException.StatusCode.Value).ToString(CultureInfo.InvariantCulture)
                : "HttpRequestException: transport failure";
        }

        return exception.GetType().Name + ": evaluator operation failed";
    }

    public static string AttemptDeadline(TimeSpan value) =>
        "attempt deadline exceeded after " + (value.TotalMinutes >= 1
            ? value.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " minutes"
            : value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " seconds");

    public static string Bound(string value) =>
        value.Length <= 400 ? value : value[..400];
}
