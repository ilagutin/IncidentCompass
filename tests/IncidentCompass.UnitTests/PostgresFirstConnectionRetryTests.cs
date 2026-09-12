using System.Net.Sockets;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Postgres;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Covers the startup connection budget without a database or a real clock. The virtual
/// <see cref="TimeProvider"/> below advances only when the code under test asks to wait, so every
/// assertion about attempts, delays and the expiry is exact rather than timing-dependent.
/// </summary>
public sealed class PostgresFirstConnectionRetryTests
{
    [Fact]
    public async Task OpenAsync_RetriesRefusedConnection_UntilItSucceeds()
    {
        var time = new VirtualTimeProvider();
        var retry = CreateRetry(time, maxAttempts: 5, initialDelayMs: 250, maxDelayMs: 2000);
        var attempts = 0;

        using var connection = await retry.OpenAsync(
            _ =>
            {
                attempts++;
                return attempts <= 2
                    ? throw ConnectionRefused()
                    : Task.FromResult(new NpgsqlConnection());
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(connection);
        Assert.Equal(3, attempts);
        Assert.Equal(
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500)],
            time.Delays);
    }

    [Fact]
    public async Task OpenAsync_StopsAtMaxAttempts_AndRethrowsTheLastFailureUnchanged()
    {
        var time = new VirtualTimeProvider();
        var retry = CreateRetry(time, maxAttempts: 3, initialDelayMs: 250, maxDelayMs: 2000);
        var attempts = 0;
        NpgsqlException? lastFailure = null;

        var thrown = await Assert.ThrowsAsync<NpgsqlException>(() => retry.OpenAsync(
            _ =>
            {
                attempts++;
                lastFailure = ConnectionRefused();
                throw lastFailure;
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(3, attempts);
        Assert.Same(lastFailure, thrown);
    }

    [Fact]
    public async Task OpenAsync_StopsBeforeMaxAttempts_WhenTheTotalDurationBudgetExpires()
    {
        var time = new VirtualTimeProvider();
        var retry = CreateRetry(
            time,
            maxAttempts: 100,
            initialDelayMs: 400,
            maxDelayMs: 400,
            maxTotalDurationSeconds: 1);
        var attempts = 0;

        await Assert.ThrowsAsync<NpgsqlException>(() => retry.OpenAsync(
            _ =>
            {
                attempts++;
                throw ConnectionRefused();
            },
            TestContext.Current.CancellationToken));

        // Two waits of 400ms fit inside the one-second expiry; a third would cross it, so the
        // budget ends the loop long before the 100 attempts it was also allowed.
        Assert.Equal(3, attempts);
        Assert.Equal(2, time.Delays.Count);
    }

    [Fact]
    public async Task OpenAsync_DoesNotRetry_AFailureWaitingCannotFix()
    {
        var time = new VirtualTimeProvider();
        var retry = CreateRetry(time, maxAttempts: 5, initialDelayMs: 250, maxDelayMs: 2000);
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => retry.OpenAsync(
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("The connection string is not usable.");
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Empty(time.Delays);
    }

    /// <summary>
    /// The shape a database that is up but still starting returns: the server answered, so there is
    /// no socket error to see, only SQLSTATE 57P03.
    /// </summary>
    [Fact]
    public async Task OpenAsync_Retries_APostgresServerThatIsStillStarting()
    {
        var time = new VirtualTimeProvider();
        var retry = CreateRetry(time, maxAttempts: 5, initialDelayMs: 250, maxDelayMs: 2000);
        var attempts = 0;

        using var connection = await retry.OpenAsync(
            _ =>
            {
                attempts++;
                return attempts == 1
                    ? throw new PostgresException(
                        "the database system is starting up",
                        "FATAL",
                        "FATAL",
                        "57P03")
                    : Task.FromResult(new NpgsqlConnection());
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(connection);
        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], time.Delays);
    }

    [Fact]
    public async Task OpenAsync_Retries_ATimeoutReachingTheEndpoint()
    {
        var time = new VirtualTimeProvider();
        var retry = CreateRetry(time, maxAttempts: 5, initialDelayMs: 250, maxDelayMs: 2000);
        var attempts = 0;

        using var connection = await retry.OpenAsync(
            _ =>
            {
                attempts++;
                return attempts == 1
                    ? throw new TimeoutException("The connection attempt timed out.")
                    : Task.FromResult(new NpgsqlConnection());
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(connection);
        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], time.Delays);
    }

    /// <summary>
    /// PostgreSQL takes a connection off its listen backlog before the postmaster is serving, then
    /// drops it mid-handshake. Npgsql reports that as a read failure over the stream, with no
    /// socket error and no SQLSTATE to classify it by, so only the IOException arm catches it.
    /// </summary>
    [Fact]
    public async Task OpenAsync_Retries_AConnectionAcceptedThenDroppedMidHandshake()
    {
        var time = new VirtualTimeProvider();
        var retry = CreateRetry(time, maxAttempts: 5, initialDelayMs: 250, maxDelayMs: 2000);
        var attempts = 0;

        using var connection = await retry.OpenAsync(
            _ =>
            {
                attempts++;
                return attempts == 1
                    ? throw new NpgsqlException(
                        "Exception while reading from stream",
                        new EndOfStreamException("Attempted to read past the end of the stream."))
                    : Task.FromResult(new NpgsqlConnection());
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(connection);
        Assert.Equal(2, attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(250)], time.Delays);
    }

    /// <summary>
    /// A hostname that does not resolve is a typo in the connection string, not a database that is
    /// still starting. Retrying it would burn the whole budget on every single host start.
    /// </summary>
    [Fact]
    public async Task OpenAsync_DoesNotRetry_AHostnameThatDoesNotResolve()
    {
        var time = new VirtualTimeProvider();
        var retry = CreateRetry(time, maxAttempts: 5, initialDelayMs: 250, maxDelayMs: 2000);
        var attempts = 0;

        await Assert.ThrowsAsync<NpgsqlException>(() => retry.OpenAsync(
            _ =>
            {
                attempts++;
                throw new NpgsqlException(
                    "Failed to resolve the configured PostgreSQL host.",
                    new SocketException((int)SocketError.HostNotFound));
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Empty(time.Delays);
    }

    /// <summary>
    /// A server answering with an error is a running database saying no, whatever Npgsql's own
    /// transient classification says about the SQLSTATE. Only 57P03, "the server is still
    /// starting", is a reason to wait.
    /// </summary>
    [Fact]
    public async Task OpenAsync_DoesNotRetry_APostgresErrorOtherThanCannotConnectNow()
    {
        var time = new VirtualTimeProvider();
        var retry = CreateRetry(time, maxAttempts: 5, initialDelayMs: 250, maxDelayMs: 2000);
        var attempts = 0;

        await Assert.ThrowsAsync<PostgresException>(() => retry.OpenAsync(
            _ =>
            {
                attempts++;

                // 53300 is too_many_connections, which Npgsql reports as transient.
                throw new PostgresException(
                    "sorry, too many clients already",
                    "FATAL",
                    "FATAL",
                    "53300");
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(1, attempts);
        Assert.Empty(time.Delays);
    }

    [Fact]
    public async Task OpenAsync_DoesNotRetry_AfterTheFirstConnectionHasOpened()
    {
        var time = new VirtualTimeProvider();
        var retry = CreateRetry(time, maxAttempts: 5, initialDelayMs: 250, maxDelayMs: 2000);
        var attempts = 0;

        using (await retry.OpenAsync(
                   _ =>
                   {
                       attempts++;
                       return Task.FromResult(new NpgsqlConnection());
                   },
                   TestContext.Current.CancellationToken))
        {
        }

        await Assert.ThrowsAsync<NpgsqlException>(() => retry.OpenAsync(
            _ =>
            {
                attempts++;
                throw ConnectionRefused();
            },
            TestContext.Current.CancellationToken));

        // One successful attempt plus one failed attempt: the latch means the refusal after the
        // first success is a steady-state failure and is not waited on at all.
        Assert.Equal(2, attempts);
        Assert.Empty(time.Delays);
    }

    private static PostgresFirstConnectionRetry CreateRetry(
        TimeProvider timeProvider,
        int maxAttempts,
        int initialDelayMs,
        int maxDelayMs,
        int maxTotalDurationSeconds = 30)
    {
        var options = Options.Create(new PostgresOptions
        {
            StartupRetry = new PostgresStartupRetryOptions
            {
                MaxAttempts = maxAttempts,
                InitialDelayMilliseconds = initialDelayMs,
                MaxDelayMilliseconds = maxDelayMs,
                MaxTotalDurationSeconds = maxTotalDurationSeconds
            }
        });

        return new PostgresFirstConnectionRetry(options, timeProvider);
    }

    /// <summary>
    /// The shape Npgsql produces when nothing is listening on the configured port: the transport
    /// failure is the inner exception, not the outer one.
    /// </summary>
    private static NpgsqlException ConnectionRefused() =>
        new(
            "Failed to connect to the configured PostgreSQL endpoint.",
            new SocketException((int)SocketError.ConnectionRefused));

    /// <summary>
    /// A clock that only moves when the code under test asks to wait. <c>CreateTimer</c> records
    /// the requested wait, advances the virtual clock by exactly that much and then completes on
    /// the thread pool, so <c>Task.Delay</c> resolves without spending real time and the elapsed
    /// measurement still sees the full delay.
    /// </summary>
    private sealed class VirtualTimeProvider : TimeProvider
    {
        private long ticks;

        public List<TimeSpan> Delays { get; } = [];

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Read(ref ticks));

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            Delays.Add(dueTime);
            Interlocked.Add(ref ticks, dueTime.Ticks);
            return new ImmediateTimer(callback, state);
        }
    }

    private sealed class ImmediateTimer : ITimer
    {
        private readonly Timer timer;

        public ImmediateTimer(TimerCallback callback, object? state) =>
            timer = new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

        public void Dispose() => timer.Dispose();

        public ValueTask DisposeAsync() => timer.DisposeAsync();
    }
}
