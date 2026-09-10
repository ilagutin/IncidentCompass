using IncidentCompass.Application.Core.Exceptions;
using IncidentCompass.Application.Governance.PostReportActions;

namespace IncidentCompass.Worker;

internal sealed class PostReportActionEvaluationPump(
    IServiceScopeFactory serviceScopeFactory,
    PostReportActionWorkflowCatalog catalog,
    PostReportActionEvaluationLeaseRenewer leaseRenewer,
    ILogger<PostReportActionEvaluationPump> logger)
{
    private readonly ClaimedTaskSet activeEvaluations = PostReportActionEvaluationTaskSet.Create(logger);

    public int ActiveEvaluationCount => activeEvaluations.Count;

    public Task ObserveCompletedAsync(CancellationToken cancellationToken) =>
        activeEvaluations.ObserveCompletedAsync(cancellationToken);

    public Task DrainAsync() => activeEvaluations.DrainAsync();

    public async Task<int> FillAvailableSlotsAsync(
        string workerId,
        PostReportActionEvaluationOptions options,
        CancellationToken cancellationToken)
    {
        var available = options.MaxConcurrency - activeEvaluations.Count;
        if (available <= 0)
        {
            return 0;
        }

        IReadOnlyList<PostReportActionIntentCandidate> candidates;
        using (var scope = serviceScopeFactory.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IPostReportActionIntentRepository>();
            candidates = await repository.FindCandidatesAsync(
                Math.Min(available, options.ScanBatchSize), cancellationToken);
        }

        foreach (var candidate in candidates)
        {
            var evaluationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                activeEvaluations.Add(
                    ProcessCandidateAsync(workerId, options, candidate, evaluationCancellation.Token),
                    evaluationCancellation);
            }
            catch
            {
                evaluationCancellation.Dispose();
                throw;
            }
        }

        return candidates.Count;
    }

    public Task WaitForNextWakeAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        activeEvaluations.WaitForNextWakeAsync(delay, cancellationToken);

    private async Task ProcessCandidateAsync(
        string workerId,
        PostReportActionEvaluationOptions options,
        PostReportActionIntentCandidate candidate,
        CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IPostReportActionIntentRepository>();
        var lease = TimeSpan.FromSeconds(options.LeaseSeconds);
        var claim = await repository.TryClaimAsync(
            candidate.IntentId, workerId, lease, options.MaximumAttempts, cancellationToken);
        if (claim is null)
        {
            return;
        }

        if (!claim.Intent.HasValidCanonicalInput())
        {
            await repository.DeadLetterAsync(
                claim.Intent.Id, claim.Fence, "workflow_input_invalid", cancellationToken);
            return;
        }

        if (!catalog.TryGet(claim.Intent.ToolId, claim.Intent.WorkflowVersion, out var workflow))
        {
            await repository.DeadLetterAsync(
                claim.Intent.Id, claim.Fence, "workflow_not_registered", cancellationToken);
            return;
        }

        PostReportActionWorkflowResult? result;
        try
        {
            result = await leaseRenewer.EvaluateAsync(
                repository, workflow, claim, workerId, lease, cancellationToken);
        }
        catch (PersistenceException)
        {
            await RetryAsync(repository, claim, options, "workflow_persistence_failed", cancellationToken);
            return;
        }
        catch (TimeoutException)
        {
            await RetryAsync(repository, claim, options, "workflow_timeout", cancellationToken);
            return;
        }

        if (result is null)
        {
            return;
        }

        if (!IsValidCode(result.Code) || (result.IsCompleted && result.ShouldRetry))
        {
            await repository.DeadLetterAsync(
                claim.Intent.Id, claim.Fence, "workflow_result_invalid", cancellationToken);
        }
        else if (result.IsCompleted)
        {
            await repository.CompleteAsync(
                claim.Intent.Id, claim.Fence,
                result.Code is "completed" or "approved" or "requested" ? null : result.Code,
                cancellationToken);
        }
        else if (result.ShouldRetry)
        {
            await RetryAsync(repository, claim, options, result.Code, cancellationToken);
        }
        else
        {
            await repository.DeadLetterAsync(
                claim.Intent.Id, claim.Fence, result.Code, cancellationToken);
        }
    }

    private static Task<bool> RetryAsync(
        IPostReportActionIntentRepository repository,
        PostReportActionIntentClaim claim,
        PostReportActionEvaluationOptions options,
        string code,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(
            claim.Intent.AttemptCount <= 1
                ? options.FirstRetryDelaySeconds
                : options.SecondRetryDelaySeconds);
        return repository.RetryAsync(
            claim.Intent.Id, claim.Fence, code, delay, options.MaximumAttempts, cancellationToken);
    }

    private static bool IsValidCode(string code) =>
        code is { Length: >= 1 and <= 128 } &&
        !code.StartsWith("internal_", StringComparison.Ordinal) &&
        code != "attempts_exhausted" &&
        code.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
