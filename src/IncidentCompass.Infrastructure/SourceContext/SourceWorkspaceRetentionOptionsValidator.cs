using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.SourceContext;

internal sealed class SourceWorkspaceRetentionOptionsValidator
    : IValidateOptions<SourceWorkspaceRetentionOptions>
{
    // One hour is the floor rather than zero, for the same reason the row retention windows have
    // one: the operation is an irreversible recursive delete, and "as soon as it exists" must not be
    // what an empty or mistyped setting becomes. An hour still leaves roughly two orders of
    // magnitude between the window and the longest a live workspace can exist. A week is the ceiling
    // because a leftover is dead disk, and a window longer than that stops being retention.
    private const int MinimumRetentionHours = 1;
    private const int MaximumRetentionHours = 168;
    private const int MinimumDirectoriesPerRun = 1;
    private const int MaximumDirectoriesPerRun = 10_000;

    public ValidateOptionsResult Validate(string? name, SourceWorkspaceRetentionOptions options)
    {
        var failures = new List<string>();
        if (options.RetentionHours < MinimumRetentionHours ||
            options.RetentionHours > MaximumRetentionHours)
        {
            failures.Add(
                $"{SourceWorkspaceRetentionOptions.SectionName}:{nameof(options.RetentionHours)} must be between " +
                $"{MinimumRetentionHours} and {MaximumRetentionHours}.");
        }

        if (options.MaxDirectoriesPerRun < MinimumDirectoriesPerRun ||
            options.MaxDirectoriesPerRun > MaximumDirectoriesPerRun)
        {
            failures.Add(
                $"{SourceWorkspaceRetentionOptions.SectionName}:{nameof(options.MaxDirectoriesPerRun)} must be between " +
                $"{MinimumDirectoriesPerRun} and {MaximumDirectoriesPerRun}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
