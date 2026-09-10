using Microsoft.Extensions.Options;

namespace IncidentCompass.Worker;

internal sealed class WorkerOptionsValidator : IValidateOptions<WorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkerOptions options)
    {
        var failures = new List<string>();
        AddPositiveFailure(failures, options.MaxConcurrentJobs, nameof(options.MaxConcurrentJobs));
        AddPositiveFailure(failures, options.PollIntervalSeconds, nameof(options.PollIntervalSeconds));
        AddPositiveFailure(failures, options.LeaseSeconds, nameof(options.LeaseSeconds));
        AddPositiveFailure(failures, options.MaxAttempts, nameof(options.MaxAttempts));
        AddNonNegativeFailure(failures, options.RetryDelaySeconds, nameof(options.RetryDelaySeconds));

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void AddPositiveFailure(List<string> failures, int value, string name)
    {
        if (value <= 0)
        {
            failures.Add($"{WorkerOptions.SectionName}:{name} must be greater than zero.");
        }
    }

    private static void AddNonNegativeFailure(List<string> failures, int value, string name)
    {
        if (value < 0)
        {
            failures.Add($"{WorkerOptions.SectionName}:{name} must be zero or greater.");
        }
    }
}
