using Microsoft.Extensions.Options;

namespace IncidentCompass.Worker;

internal sealed class RetentionScheduleOptionsValidator : IValidateOptions<RetentionScheduleOptions>
{
    // A zero or negative interval is rejected rather than clamped, for the same reason the retention
    // windows are: the operations are destructive, and "as fast as the loop can go" is what an empty
    // or mistyped setting would otherwise become. The upper bound is a day, because an interval
    // longer than that stops being a schedule and should be expressed by turning retention off.
    private const int MinimumIntervalMinutes = 1;
    private const int MaximumIntervalMinutes = 1440;

    public ValidateOptionsResult Validate(string? name, RetentionScheduleOptions options)
    {
        if (options.IntervalMinutes < MinimumIntervalMinutes ||
            options.IntervalMinutes > MaximumIntervalMinutes)
        {
            return ValidateOptionsResult.Fail(
                $"{RetentionScheduleOptions.SectionName}:{nameof(options.IntervalMinutes)} must be between " +
                $"{MinimumIntervalMinutes} and {MaximumIntervalMinutes}.");
        }

        return ValidateOptionsResult.Success;
    }
}
