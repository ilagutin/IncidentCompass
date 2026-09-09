namespace IncidentCompass.Tester;

internal sealed record TesterOptions(
    Uri BaseUrl,
    Uri PublicBaseUrl,
    TimeSpan RequestTimeout,
    TimeSpan PollTimeout,
    TimeSpan PollInterval,
    TimeSpan ScenarioTimeout,
    TimeSpan TotalTimeout)
{
    public static TesterOptions Parse(
        string[] args,
        string? environmentBaseUrl,
        string? environmentPublicBaseUrl,
        string? environmentRequestTimeoutSeconds,
        string? environmentPollTimeoutSeconds,
        string? environmentPollIntervalSeconds)
    {
        var baseUrl = environmentBaseUrl;
        var publicBaseUrl = environmentPublicBaseUrl;
        var requestTimeout = ReadOptionalPositiveSeconds(environmentRequestTimeoutSeconds, "INCIDENTCOMPASS_TESTER_REQUEST_TIMEOUT_SECONDS") ?? TimeSpan.FromSeconds(30);
        var pollTimeout = ReadOptionalPositiveSeconds(environmentPollTimeoutSeconds, "INCIDENTCOMPASS_TESTER_POLL_TIMEOUT_SECONDS") ?? TimeSpan.FromMinutes(12);
        var pollInterval = ReadOptionalPositiveSeconds(environmentPollIntervalSeconds, "INCIDENTCOMPASS_TESTER_POLL_INTERVAL_SECONDS") ?? TimeSpan.FromSeconds(2);
        var scenarioTimeout = TimeSpan.FromMinutes(13);
        var totalTimeout = TimeSpan.FromMinutes(75);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (TryReadOptionValue(args, ref index, "--base-url", out var baseUrlValue))
            {
                baseUrl = baseUrlValue;
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--public-base-url", out var publicBaseUrlValue))
            {
                publicBaseUrl = publicBaseUrlValue;
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--request-timeout-seconds", out var requestTimeoutValue))
            {
                requestTimeout = ReadPositiveSeconds(requestTimeoutValue, "--request-timeout-seconds");
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--poll-timeout-seconds", out var pollTimeoutValue))
            {
                pollTimeout = ReadPositiveSeconds(pollTimeoutValue, "--poll-timeout-seconds");
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--poll-interval-seconds", out var pollIntervalValue))
            {
                pollInterval = ReadPositiveSeconds(pollIntervalValue, "--poll-interval-seconds");
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--scenario-timeout-seconds", out var scenarioTimeoutValue))
            {
                scenarioTimeout = ReadPositiveSeconds(scenarioTimeoutValue, "--scenario-timeout-seconds");
                continue;
            }

            if (TryReadOptionValue(args, ref index, "--total-timeout-seconds", out var totalTimeoutValue))
            {
                totalTimeout = ReadPositiveSeconds(totalTimeoutValue, "--total-timeout-seconds");
                continue;
            }

            Console.Error.WriteLine("Warning: unknown tester argument '" + argument + "'.");
        }

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "http://localhost:5198";
        }

        if (string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            publicBaseUrl = baseUrl;
        }

        return new TesterOptions(
            ToBaseUri(baseUrl),
            ToBaseUri(publicBaseUrl),
            requestTimeout,
            pollTimeout,
            pollInterval,
            scenarioTimeout,
            totalTimeout);
    }

    private static bool TryReadOptionValue(
        string[] args,
        ref int index,
        string optionName,
        out string value)
    {
        value = string.Empty;
        if (!string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine("Warning: tester argument '" + optionName + "' is missing a value.");
            return true;
        }

        value = args[index + 1];
        index++;
        return true;
    }

    private static TimeSpan? ReadOptionalPositiveSeconds(string? value, string optionName)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : ReadPositiveSeconds(value, optionName);
    }

    private static TimeSpan ReadPositiveSeconds(string value, string optionName)
    {
        if (!int.TryParse(value, out var seconds) || seconds <= 0)
        {
            throw new ArgumentException(optionName + " must be a positive integer number of seconds.");
        }

        return TimeSpan.FromSeconds(seconds);
    }

    private static Uri ToBaseUri(string value) => new(value.TrimEnd('/') + "/");
}
