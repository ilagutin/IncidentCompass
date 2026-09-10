using IncidentCompass.Application.Core.Observability;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Worker;

internal sealed partial class WorkerJobPump(
    IServiceScopeFactory serviceScopeFactory,
    WorkerJobLeaseRenewer leaseRenewer,
    ILogger<WorkerJobPump> logger,
    IProviderOutageTracker? providerOutageTracker = null,
    IRuntimeTelemetry? telemetry = null)
{
    private readonly WorkerJobTaskSet activeJobs = new(logger);

    public int ActiveJobCount => activeJobs.Count;

    public Task ObserveCompletedAsync(CancellationToken cancellationToken) => activeJobs.ObserveCompletedAsync(cancellationToken);

    public Task DrainAsync() => activeJobs.DrainAsync();

    public async Task<int> FillAvailableSlotsAsync(
        string workerId,
        WorkerOptions options,
        CancellationToken cancellationToken)
    {
        var started = 0;
        while (providerOutageTracker?.IsBackpressured != true && activeJobs.Count < options.MaxConcurrentJobs)
        {
            var job = await ClaimNextAsync(workerId, options, cancellationToken);
            if (job is null)
            {
                break;
            }

            var jobCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                activeJobs.Add(ProcessClaimedAsync(workerId, options, job, jobCancellation.Token), jobCancellation);
                started++;
            }
            catch
            {
                jobCancellation.Dispose();
                throw;
            }
        }

        return started;
    }

    public Task WaitForNextWakeAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        activeJobs.WaitForNextWakeAsync(delay, cancellationToken);

    private async Task<TriageJob?> ClaimNextAsync(
        string workerId,
        WorkerOptions options,
        CancellationToken cancellationToken)
    {
        using var claimTelemetry = telemetry?.StartJobClaim();
        using var scope = serviceScopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var job = await runner.ClaimNextAsync(workerId, TimeSpan.FromSeconds(options.LeaseSeconds), cancellationToken);
        if (job is not null)
        {
            telemetry?.RecordJobClaim();
        }
        return job;
    }

    private async Task ProcessClaimedAsync(
        string workerId,
        WorkerOptions options,
        TriageJob job,
        CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<ITriageJobRunner>();
        var leaseDuration = TimeSpan.FromSeconds(options.LeaseSeconds);
        using var processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewalTask = leaseRenewer.RenewUntilStoppedAsync(runner, job, workerId, leaseDuration, renewalCancellation.Token);
        var processingTask = runner.ProcessClaimedAsync(
            job,
            workerId,
            new TriageJobProcessingSettings(options.MaxAttempts, TimeSpan.FromSeconds(options.RetryDelaySeconds)),
            processingCancellation.Token);

        if (await Task.WhenAny(processingTask, renewalTask) == processingTask)
        {
            renewalCancellation.Cancel();
            await ObserveRenewalStopAsync(renewalTask, cancellationToken);
            await processingTask;
            return;
        }

        await CancelProcessingAfterLeaseLossAsync(job, processingTask, renewalTask, processingCancellation, cancellationToken);
    }

    private async Task CancelProcessingAfterLeaseLossAsync(
        TriageJob job,
        Task processingTask,
        Task renewalTask,
        CancellationTokenSource processingCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            // The renewal loop only returns when this worker no longer owns the lease.
            await renewalTask;
            LogLeaseOwnershipLost(logger, job.Id, job.Attempt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            processingCancellation.Cancel();
            await ObserveProcessingStopAsync(processingTask);
            throw;
        }
        catch (Exception exception)
        {
            LogLeaseRenewalFailed(logger, exception, job.Id, job.Attempt);
        }

        processingCancellation.Cancel();
        await ObserveProcessingStopAsync(processingTask);
    }

    private static async Task ObserveRenewalStopAsync(Task renewalTask, CancellationToken cancellationToken)
    {
        try
        {
            await renewalTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task ObserveProcessingStopAsync(Task processingTask)
    {
        try
        {
            await processingTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(
        EventId = 1101,
        Level = LogLevel.Warning,
        Message = "Triage job {JobId} attempt {Attempt} lost lease ownership; cancelling in-flight work.")]
    private static partial void LogLeaseOwnershipLost(ILogger logger, Guid jobId, int attempt);

    [LoggerMessage(
        EventId = 1102,
        Level = LogLevel.Warning,
        Message = "Triage job {JobId} attempt {Attempt} lease renewal failed; cancelling in-flight work.")]
    private static partial void LogLeaseRenewalFailed(ILogger logger, Exception exception, Guid jobId, int attempt);
}
