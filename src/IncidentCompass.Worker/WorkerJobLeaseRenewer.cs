using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Worker;

internal sealed class WorkerJobLeaseRenewer
{
    private static readonly TimeSpan MinimumRenewalInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Renews the job lease until ownership is lost, renewal fails or the caller cancels.
    /// The returned task completing successfully is itself the lease-loss signal: the loop only
    /// exits when the store reports that this worker no longer owns the lease. It faults when
    /// renewal throws and cancels with <paramref name="cancellationToken"/>, so callers must treat
    /// normal completion as "the lease is gone" and stop the work it protected.
    /// </summary>
    public async Task RenewUntilStoppedAsync(
        ITriageJobRunner runner,
        TriageJob job,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var interval = CalculateRenewalInterval(leaseDuration);
        while (true)
        {
            await Task.Delay(interval, cancellationToken);
            if (!await runner.RenewLeaseAsync(job, workerId, leaseDuration, cancellationToken))
            {
                return;
            }
        }
    }

    private static TimeSpan CalculateRenewalInterval(TimeSpan leaseDuration)
    {
        var oneThird = TimeSpan.FromTicks(leaseDuration.Ticks / 3);
        return oneThird >= MinimumRenewalInterval ? oneThird : MinimumRenewalInterval;
    }
}
