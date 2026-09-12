using Microsoft.Extensions.Options;

namespace IncidentCompass.Application.Core.Configuration;

internal sealed class RetentionOptionsValidator : IValidateOptions<RetentionOptions>
{
    // A zero-day window is rejected rather than treated as "retain nothing". Both operations are
    // destructive and irreversible, so the configuration that empties a payload the moment it lands
    // has to be spelled out as a deliberate one-day minimum instead of being the value an empty or
    // mistyped setting falls back to.
    private const int MinimumRetentionDays = 1;
    private const int MaximumRetentionDays = 3650;
    private const int MinimumRowsPerRun = 1;
    private const int MaximumRowsPerRun = 100_000;

    public ValidateOptionsResult Validate(string? name, RetentionOptions options)
    {
        if (options.SignalPayloadRetentionDays < MinimumRetentionDays ||
            options.SignalPayloadRetentionDays > MaximumRetentionDays)
        {
            return ValidateOptionsResult.Fail(
                $"SignalPayloadRetentionDays must be between {MinimumRetentionDays} and {MaximumRetentionDays}.");
        }

        if (options.AttemptArtifactRetentionDays < MinimumRetentionDays ||
            options.AttemptArtifactRetentionDays > MaximumRetentionDays)
        {
            return ValidateOptionsResult.Fail(
                $"AttemptArtifactRetentionDays must be between {MinimumRetentionDays} and {MaximumRetentionDays}.");
        }

        if (options.MaxRowsPerRun < MinimumRowsPerRun || options.MaxRowsPerRun > MaximumRowsPerRun)
        {
            return ValidateOptionsResult.Fail(
                $"MaxRowsPerRun must be between {MinimumRowsPerRun} and {MaximumRowsPerRun}.");
        }

        return ValidateOptionsResult.Success;
    }
}
