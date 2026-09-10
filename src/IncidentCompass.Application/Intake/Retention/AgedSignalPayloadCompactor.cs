using IncidentCompass.Application.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Application.Intake.Retention;

/// <summary>
/// One bounded run of raw signal payload compaction: turns the configured retention window into a
/// cutoff and hands it to the persistence port. It is a plain callable operation with no schedule of
/// its own, so a host, a test or an operator-facing entry point can each drive it the same way.
/// </summary>
public sealed partial class AgedSignalPayloadCompactor(
    ISignalPayloadCompactionRepository repository,
    IOptions<RetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<AgedSignalPayloadCompactor> logger)
{
    public async Task<int> CompactAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var receivedBeforeUtc = timeProvider.GetUtcNow()
            .AddDays(-settings.SignalPayloadRetentionDays);

        var compacted = await repository.CompactAsync(
            receivedBeforeUtc,
            settings.MaxRowsPerRun,
            cancellationToken);

        LogCompacted(logger, compacted, receivedBeforeUtc);

        return compacted;
    }

    [LoggerMessage(
        EventId = 3701,
        Level = LogLevel.Information,
        Message = "Signal payload compaction emptied {CompactedSignals} raw signal payload(s) received before {ReceivedBeforeUtc}")]
    private static partial void LogCompacted(
        ILogger logger,
        int compactedSignals,
        DateTimeOffset receivedBeforeUtc);
}
