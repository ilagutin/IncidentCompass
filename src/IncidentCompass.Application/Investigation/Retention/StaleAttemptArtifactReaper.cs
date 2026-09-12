using IncidentCompass.Application.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Application.Investigation.Retention;

/// <summary>
/// One bounded run of attempt-artifact reaping: turns the configured retention window into a cutoff
/// and hands it to the persistence port. It is a plain callable operation with no schedule of its
/// own, so a host, a test or an operator-facing entry point can each drive it the same way.
/// </summary>
public sealed partial class StaleAttemptArtifactReaper(
    IAttemptArtifactRetentionRepository repository,
    IOptions<RetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<StaleAttemptArtifactReaper> logger)
{
    public async Task<int> ReapAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var createdBeforeUtc = timeProvider.GetUtcNow()
            .AddDays(-settings.AttemptArtifactRetentionDays);

        var reaped = await repository.ReapAsync(
            createdBeforeUtc,
            settings.MaxRowsPerRun,
            cancellationToken);

        LogReaped(logger, reaped, createdBeforeUtc);

        return reaped;
    }

    [LoggerMessage(
        EventId = 3702,
        Level = LogLevel.Information,
        Message = "Attempt artifact retention reaped {ReapedArtifacts} artifact(s) created before {CreatedBeforeUtc}")]
    private static partial void LogReaped(
        ILogger logger,
        int reapedArtifacts,
        DateTimeOffset createdBeforeUtc);
}
