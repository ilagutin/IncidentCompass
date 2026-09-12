using System.Net.Sockets;
using IncidentCompass.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IncidentCompass.Infrastructure.Postgres;

/// <summary>
/// Waits, within a configured budget, for the database to start accepting connections - and only
/// for the first one.
/// <para>
/// Both hosts reach PostgreSQL while they are still starting: migrations run from a hosted service
/// and the triage configuration persists its snapshot from a warmup hosted service, both at host
/// start. A database that is not accepting connections at that moment refuses the very first one
/// and the host dies. The shipped Compose files gate both hosts on a PostgreSQL health check, so
/// they are not the case this guards; a host started outside them, and the window between that
/// health gate passing and the first connection actually being made, are. Restarting the process
/// eventually works, but that makes the container restart policy the thing implementing startup
/// ordering, which is neither configurable nor visible from inside the application. This type makes
/// the wait an explicit bounded budget instead.
/// </para>
/// <para>
/// The retry is latched to the first success on purpose. Once one connection has opened, the
/// ordering question is answered for the lifetime of the process, and every later call goes
/// straight to the provider with no retry and no delay. A database that disappears afterwards is a
/// genuine failure, and a steady-state retry here would hide it behind repeated silent waits and
/// hold a request or a claimed job open while it did so.
/// </para>
/// <para>
/// Only failures that mean "not listening yet, or still starting up" are retried, and
/// <see cref="IsDatabaseNotListeningYet"/> spells out exactly which ones. A wrong credential, a
/// missing database, a hostname that does not resolve or an unparsable connection string is
/// rethrown on the first attempt, because waiting cannot change any of them.
/// </para>
/// <para>
/// When the budget runs out the last failure is rethrown unchanged rather than wrapped in a new
/// exception type. <see cref="PostgresOperation"/> sits outside this type and is the single place
/// that normalizes a persistence failure for the port contract; introducing a second exception type
/// here would either escape that normalization or be normalized twice, and would lose the provider
/// detail that says why the wait never succeeded.
/// </para>
/// </summary>
internal sealed partial class PostgresFirstConnectionRetry(
    IOptions<PostgresOptions> options,
    TimeProvider timeProvider,
    ILogger<PostgresFirstConnectionRetry>? logger = null)
{
    /// <summary>
    /// <c>cannot_connect_now</c>: PostgreSQL is up but still recovering or starting, so the next
    /// attempt has a real chance of succeeding.
    /// </summary>
    private const string CannotConnectNowSqlState = "57P03";

    private readonly ILogger logger = logger ?? NullLogger<PostgresFirstConnectionRetry>.Instance;

    // Read before anything else on every call, so the steady-state path costs one volatile read.
    // A lost race between two concurrent first callers only makes one extra call retry-eligible,
    // which is harmless: it can delay a failure that was going to be retried anyway, never change
    // what the caller gets back.
    private volatile bool firstConnectionOpened;

    /// <summary>
    /// Opens a connection through <paramref name="open"/>, retrying it within the configured budget
    /// until the first one succeeds.
    /// </summary>
    public async Task<NpgsqlConnection> OpenAsync(
        Func<CancellationToken, Task<NpgsqlConnection>> open,
        CancellationToken cancellationToken)
    {
        if (firstConnectionOpened)
        {
            return await open(cancellationToken);
        }

        var budget = options.Value.StartupRetry;
        var startedAt = timeProvider.GetTimestamp();
        var expiry = TimeSpan.FromSeconds(budget.MaxTotalDurationSeconds);
        var maximumDelay = TimeSpan.FromMilliseconds(budget.MaxDelayMilliseconds);
        var delay = TimeSpan.FromMilliseconds(budget.InitialDelayMilliseconds);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var connection = await open(cancellationToken);
                firstConnectionOpened = true;
                return connection;
            }
            catch (Exception exception) when (
                !cancellationToken.IsCancellationRequested &&
                IsDatabaseNotListeningYet(exception))
            {
                var elapsed = timeProvider.GetElapsedTime(startedAt);

                // The expiry is checked here, between attempts, and includes the delay about to be
                // taken: sleeping past it would spend the caller's time on an attempt the budget
                // has already ruled out. It does not bound an attempt already in flight, so the
                // worst-case wall time is the expiry plus one connection timeout.
                if (attempt >= budget.MaxAttempts || elapsed + delay >= expiry)
                {
                    LogStartupBudgetExhausted(
                        logger,
                        attempt,
                        (long)elapsed.TotalMilliseconds,
                        exception.GetType().Name);
                    throw;
                }

                LogStartupConnectionRetried(
                    logger,
                    attempt,
                    (long)delay.TotalMilliseconds,
                    exception.GetType().Name);
                await Task.Delay(delay, timeProvider, cancellationToken);
                delay = NextDelay(delay, maximumDelay);
            }
        }
    }

    private static TimeSpan NextDelay(TimeSpan delay, TimeSpan maximumDelay)
    {
        var doubled = delay + delay;
        return doubled > maximumDelay ? maximumDelay : doubled;
    }

    /// <summary>
    /// Decides whether waiting could plausibly change the outcome. Exactly four failures qualify:
    /// <list type="bullet">
    /// <item>a <see cref="SocketException"/> other than a name that does not resolve, which is what
    /// an endpoint with nothing listening on it produces;</item>
    /// <item>an <see cref="IOException"/>, which is the connection being accepted and then dropped
    /// mid-handshake. PostgreSQL takes the connection off its listen backlog before the postmaster
    /// is serving, and Npgsql reports the drop as an <see cref="EndOfStreamException"/> with no
    /// socket error and no SQLSTATE. A userland proxy in front of the port produces the same shape.
    /// Retrying it cannot mask a steady-state fault, because this predicate only ever runs before
    /// the first successful connection;</item>
    /// <item>a <see cref="TimeoutException"/> reaching that endpoint;</item>
    /// <item>PostgreSQL answering <c>57P03</c>, which is the server saying it is up but still
    /// starting.</item>
    /// </list>
    /// <para>
    /// Everything else is permanent here and is rethrown on the first attempt. That deliberately
    /// includes the failures Npgsql reports as transient - too many connections, a full disk, a
    /// serialization conflict - because those are a database that is running and saying no, not a
    /// database that has not started. It also includes <see cref="SocketError.HostNotFound"/> and
    /// <see cref="SocketError.NoData"/>: a hostname that does not resolve is a misconfiguration,
    /// and waiting on it would burn the whole budget on every host start.
    /// </para>
    /// <para>
    /// The walk follows <see cref="Exception.InnerException"/> only. It does not fan out into an
    /// <see cref="AggregateException"/>'s inner exceptions, so a multi-host attempt that aggregates
    /// its failures is not retried. That is the fail-safe direction and is deliberate: this budget
    /// exists for the single-database deployment the runbook describes.
    /// </para>
    /// </summary>
    private static bool IsDatabaseNotListeningYet(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SocketException
                {
                    SocketErrorCode: not (SocketError.HostNotFound or SocketError.NoData)
                })
            {
                return true;
            }

            if (current is IOException or TimeoutException)
            {
                return true;
            }

            if (current is PostgresException postgres &&
                string.Equals(postgres.SqlState, CannotConnectNowSqlState, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // The exception type name is the only detail logged. A connection failure's message carries the
    // host, the port and sometimes the user, and docs/security-model.md forbids putting any of that
    // in a log line.
    [LoggerMessage(
        EventId = 2601,
        Level = LogLevel.Warning,
        Message = "PostgreSQL is not accepting connections yet ({ExceptionType}); attempt {Attempt} failed and the next one starts in {DelayMs}ms.")]
    private static partial void LogStartupConnectionRetried(
        ILogger logger,
        int attempt,
        long delayMs,
        string exceptionType);

    [LoggerMessage(
        EventId = 2602,
        Level = LogLevel.Error,
        Message = "PostgreSQL did not accept a connection within the configured startup budget: {Attempts} attempts over {ElapsedMs}ms all failed with {ExceptionType}.")]
    private static partial void LogStartupBudgetExhausted(
        ILogger logger,
        int attempts,
        long elapsedMs,
        string exceptionType);
}
