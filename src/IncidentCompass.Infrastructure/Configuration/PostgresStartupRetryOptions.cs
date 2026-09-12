namespace IncidentCompass.Infrastructure.Configuration;

/// <summary>
/// Bounds how long a host waits for the database to start listening before it gives up.
/// <para>
/// This is a startup-ordering budget, not a retry policy. The API and the Worker both touch
/// PostgreSQL while they are still starting - migrations run from a hosted service and the triage
/// configuration persists its snapshot from a warmup hosted service - so a database that is not
/// accepting connections at that moment fails the host on its first statement. Waiting for that
/// first connection here makes the
/// ordering an explicit, configured value instead of something the container restart policy
/// re-derives by killing and restarting the process.
/// </para>
/// <para>
/// The budget covers the first successful connection only. Once one connection has opened, every
/// later connection goes straight to the provider with no retry and no delay: a database that
/// disappears in steady state is a real failure and must surface as one, not be hidden behind
/// silent reconnect attempts.
/// </para>
/// <para>
/// Exhausting the budget is loud. The last failure is rethrown, normalized to the persistence
/// error the port contract declares, and the host start fails - so a genuinely absent or
/// misconfigured database is never turned into an unbounded wait.
/// </para>
/// <para>
/// Every setting is bounded at both ends by <see cref="PostgresStartupRetryOptionsValidator"/>, and
/// the ceilings matter as much as the floors. A budget of a day, or of a million attempts, is not a
/// bounded wait: it converts the loud failure above into a host that waits silently and reports
/// nothing, which is the outcome this feature exists to avoid.
/// </para>
/// </summary>
public sealed class PostgresStartupRetryOptions
{
    /// <summary>
    /// Total attempts including the first one. <c>1</c> disables retrying entirely.
    /// </summary>
    public int MaxAttempts { get; init; } = 10;

    /// <summary>
    /// Wait before the second attempt. The wait doubles after each failure.
    /// </summary>
    public int InitialDelayMilliseconds { get; init; } = 250;

    /// <summary>
    /// Ceiling for the doubling wait, so a long budget does not end in one very long sleep.
    /// </summary>
    public int MaxDelayMilliseconds { get; init; } = 2000;

    /// <summary>
    /// The expiry. The whole budget ends once this much time has passed since the first attempt,
    /// whichever bound - this one or <see cref="MaxAttempts"/> - is reached first.
    /// <para>
    /// The expiry is checked between attempts. It does not bound an attempt already in flight, so
    /// the worst-case wall time is this value plus one connection timeout.
    /// </para>
    /// </summary>
    public int MaxTotalDurationSeconds { get; init; } = 30;
}
