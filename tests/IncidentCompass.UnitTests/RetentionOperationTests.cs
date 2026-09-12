using IncidentCompass.Application.Core.Configuration;
using IncidentCompass.Application.Intake.Retention;
using IncidentCompass.Application.Investigation.Retention;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The half of each retention operation that lives above the database: turning a configured window
/// into the cutoff the persistence port is asked for. Both operations are irreversible, so the
/// arithmetic that decides how much of the past they are pointed at is worth pinning down, and so is
/// the fact that neither default is zero.
/// </summary>
public sealed class RetentionOperationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompactionAsksForTheCutoffTheConfiguredSignalWindowImplies()
    {
        var repository = new RecordingSignalPayloadCompactionRepository(compacted: 4);
        var compactor = new AgedSignalPayloadCompactor(
            repository,
            Options.Create(new RetentionOptions { SignalPayloadRetentionDays = 30, MaxRowsPerRun = 250 }),
            new FixedTimeProvider(Now),
            new RecordingLogger<AgedSignalPayloadCompactor>());

        var compacted = await compactor.CompactAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, compacted);
        Assert.Equal(Now.AddDays(-30), repository.ReceivedBeforeUtc);
        Assert.Equal(250, repository.MaxRows);
    }

    [Fact]
    public async Task ReapAsksForTheCutoffTheConfiguredArtifactWindowImplies()
    {
        var repository = new RecordingAttemptArtifactRetentionRepository(reaped: 9);
        var reaper = new StaleAttemptArtifactReaper(
            repository,
            Options.Create(new RetentionOptions { AttemptArtifactRetentionDays = 7, MaxRowsPerRun = 500 }),
            new FixedTimeProvider(Now),
            new RecordingLogger<StaleAttemptArtifactReaper>());

        var reaped = await reaper.ReapAsync(TestContext.Current.CancellationToken);

        Assert.Equal(9, reaped);
        Assert.Equal(Now.AddDays(-7), repository.CreatedBeforeUtc);
        Assert.Equal(500, repository.MaxRows);
    }

    /// <summary>
    /// The shipped defaults. The artifact window is a week rather than zero because an attempt stops
    /// being current as soon as the next one is claimed, and the artifacts of the attempt that went
    /// wrong are what an operator opens when they come to look at why.
    /// </summary>
    [Fact]
    public void DefaultsKeepSignalPayloadsForThirtyDaysAndStaleAttemptArtifactsForSeven()
    {
        var options = new RetentionOptions();

        Assert.Equal(30, options.SignalPayloadRetentionDays);
        Assert.Equal(7, options.AttemptArtifactRetentionDays);
        Assert.Equal(500, options.MaxRowsPerRun);
        Assert.True(new RetentionOptionsValidator().Validate(name: null, options).Succeeded);
    }

    /// <summary>
    /// A zero-day window has to be rejected rather than read as "retain nothing": a missing or
    /// mistyped setting must not become the configuration that empties a payload the moment it lands.
    /// </summary>
    [Theory]
    [InlineData(0, 7, 500)]
    [InlineData(-1, 7, 500)]
    [InlineData(30, 0, 500)]
    [InlineData(30, 7, 0)]
    [InlineData(30, 7, 100_001)]
    public void ValidatorRejectsAWindowOrBudgetOutsideItsRange(
        int signalDays,
        int artifactDays,
        int maxRows)
    {
        var result = new RetentionOptionsValidator().Validate(
            name: null,
            new RetentionOptions
            {
                SignalPayloadRetentionDays = signalDays,
                AttemptArtifactRetentionDays = artifactDays,
                MaxRowsPerRun = maxRows
            });

        Assert.True(result.Failed);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingSignalPayloadCompactionRepository(int compacted)
        : ISignalPayloadCompactionRepository
    {
        public DateTimeOffset? ReceivedBeforeUtc { get; private set; }

        public int? MaxRows { get; private set; }

        public Task<int> CompactAsync(
            DateTimeOffset receivedBeforeUtc,
            int maxRows,
            CancellationToken cancellationToken)
        {
            ReceivedBeforeUtc = receivedBeforeUtc;
            MaxRows = maxRows;
            return Task.FromResult(compacted);
        }
    }

    private sealed class RecordingAttemptArtifactRetentionRepository(int reaped)
        : IAttemptArtifactRetentionRepository
    {
        public DateTimeOffset? CreatedBeforeUtc { get; private set; }

        public int? MaxRows { get; private set; }

        public Task<int> ReapAsync(
            DateTimeOffset createdBeforeUtc,
            int maxRows,
            CancellationToken cancellationToken)
        {
            CreatedBeforeUtc = createdBeforeUtc;
            MaxRows = maxRows;
            return Task.FromResult(reaped);
        }
    }
}
