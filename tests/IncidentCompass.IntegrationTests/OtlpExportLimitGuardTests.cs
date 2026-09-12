using System.Globalization;
using IncidentCompass.Api;
using IncidentCompass.Application.Intake.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// exercises <see cref="OtlpExportLimitGuard" /> directly (it is internal, visible here
/// via InternalsVisibleTo) so the rejection decision and its log event can be asserted without a
/// database. The rejection log must carry the observed record count and the configured limit, and
/// must never carry record content, attributes or resource attributes.
/// </summary>
public sealed class OtlpExportLimitGuardTests
{
    private const int Limit = 3;

    [Fact]
    public void ExceedsSignalLimit_RejectionLogsTheObservedCountAndTheConfiguredLimit()
    {
        var log = new CapturingLogger();
        var guard = CreateGuard(log);

        var rejected = guard.ExceedsSignalLimit(OtlpExportTestFactory.TraceExport("otlp-guard-service", Limit + 2));

        Assert.True(rejected);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(4003, entry.EventId.Id);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("traces", entry.Message, StringComparison.Ordinal);
        Assert.Contains((Limit + 2).ToString(CultureInfo.InvariantCulture), entry.Message, StringComparison.Ordinal);
        Assert.Contains(Limit.ToString(CultureInfo.InvariantCulture), entry.Message, StringComparison.Ordinal);
        Assert.Contains("otlp_export_signal_limit_exceeded", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("otlp-guard-service", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("limit-probe", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeoutException", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceedsSignalLimit_RejectedLogExportNamesTheLogSignalKind()
    {
        var log = new CapturingLogger();
        var guard = CreateGuard(log);

        Assert.True(guard.ExceedsSignalLimit(OtlpExportTestFactory.LogExport("otlp-guard-service", Limit + 1)));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(4003, entry.EventId.Id);
        Assert.Contains("logs", entry.Message, StringComparison.Ordinal);
        Assert.Contains((Limit + 1).ToString(CultureInfo.InvariantCulture), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExceedsSignalLimit_ExportExactlyAtTheLimitIsAcceptedWithoutLogging()
    {
        var log = new CapturingLogger();
        var guard = CreateGuard(log);

        Assert.False(guard.ExceedsSignalLimit(OtlpExportTestFactory.TraceExport("otlp-guard-service", Limit)));
        Assert.False(guard.ExceedsSignalLimit(OtlpExportTestFactory.LogExport("otlp-guard-service", Limit)));
        Assert.Empty(log.Entries);
    }

    private static OtlpExportLimitGuard CreateGuard(CapturingLogger log) =>
        new(Options.Create(new IngestionLimitsOptions { MaxSignalsPerExport = Limit }), log);

    private sealed record CapturedLogEntry(EventId EventId, LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger<OtlpExportLimitGuard>
    {
        public List<CapturedLogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new CapturedLogEntry(eventId, logLevel, formatter(state, exception)));
        }
    }
}
