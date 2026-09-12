using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Configuration;

/// <summary>
/// Refuses a startup-retry budget that cannot do its job, or that would do it for far too long.
/// Every setting is bounded at both ends. The floors exist because a budget with no attempts or no
/// expiry is not a budget, and because a zero initial delay would retry as fast as the operating
/// system can refuse the port. The ceilings exist because the whole point of the feature is a
/// bounded wait: an expiry of a day, or a million attempts, turns a loud startup failure into a
/// host that sits silently waiting and never reports anything.
/// <para>
/// It validates <see cref="PostgresOptions"/> rather than <see cref="PostgresStartupRetryOptions"/>
/// even though it is named for the budget, because the budget is a bound child object and the
/// parent is the options type the host actually binds and validates on start.
/// </para>
/// <para>
/// The check runs on start rather than on first use. An unusable budget would otherwise only be
/// discovered while the host was already trying to reach a database that is not listening, which is
/// the worst possible moment to learn the wait is misconfigured.
/// </para>
/// </summary>
internal sealed class PostgresStartupRetryOptionsValidator : IValidateOptions<PostgresOptions>
{
    private const int MaximumAttempts = 100;
    private const int MaximumDelayMilliseconds = 60000;
    private const int MaximumTotalDurationSeconds = 600;

    public ValidateOptionsResult Validate(string? name, PostgresOptions options)
    {
        var budget = options.StartupRetry;
        var failures = new List<string>();

        AddRangeFailure(
            failures,
            budget.MaxAttempts,
            1,
            MaximumAttempts,
            nameof(budget.MaxAttempts),
            "; 1 disables retrying");
        AddRangeFailure(
            failures,
            budget.InitialDelayMilliseconds,
            1,
            MaximumDelayMilliseconds,
            nameof(budget.InitialDelayMilliseconds),
            ", so a failed attempt is always followed by a real wait");
        AddRangeFailure(
            failures,
            budget.MaxDelayMilliseconds,
            1,
            MaximumDelayMilliseconds,
            nameof(budget.MaxDelayMilliseconds));
        AddRangeFailure(
            failures,
            budget.MaxTotalDurationSeconds,
            1,
            MaximumTotalDurationSeconds,
            nameof(budget.MaxTotalDurationSeconds));

        if (budget.MaxDelayMilliseconds < budget.InitialDelayMilliseconds)
        {
            failures.Add(
                $"{Setting(nameof(budget.MaxDelayMilliseconds))} must not be below " +
                $"{nameof(budget.InitialDelayMilliseconds)}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void AddRangeFailure(
        List<string> failures,
        int value,
        int minimum,
        int maximum,
        string settingName,
        string reason = "")
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(
                $"{Setting(settingName)} must be between {minimum} and {maximum}{reason}.");
        }
    }

    private static string Setting(string settingName) =>
        $"{PostgresOptions.SectionName}:StartupRetry:{settingName}";
}
