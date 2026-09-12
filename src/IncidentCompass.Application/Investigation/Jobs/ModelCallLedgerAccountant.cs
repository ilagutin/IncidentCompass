using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.ModelGateway;
using IncidentCompass.Application.Core.Observability;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Turns one finished investigation model call into its durable <c>ModelCall</c> triage-ledger
/// accounting: it resolves provider-reported usage against the local estimate, builds the ledger
/// metadata for both the success and the failure shape, and writes the success row.
/// </summary>
/// <remarks>
/// The logger is the caller's own <c>ILogger</c> instance, so the completion event keeps the
/// <c>InvestigationModelCaller</c> category and the event id documented in
/// <c>docs/observability.md</c>. Only bounded call metadata is recorded here - never rendered
/// prompts, provider response bodies, credentials or embedding vectors.
/// </remarks>
internal sealed partial class ModelCallLedgerAccountant(
    TriageLedgerAppender ledgerAppender,
    ILogger logger)
{
    /// <summary>
    /// What event 3201 prints for <see cref="ModelCallLedgerMetadata.ProviderId"/> when the call's
    /// route named no configured provider, so the log line reads as a stated fact rather than an
    /// empty segment between two slashes.
    /// </summary>
    private const string UnrecordedProviderId = "unknown";

    /// <summary>
    /// Appends the success accounting row and returns the token count charged to the attempt.
    /// </summary>
    public async Task<int> RecordSuccessAsync(
        TriageJobCallContext context,
        AiModelRequest request,
        AiModelResponse response,
        Guid callId,
        TimeSpan duration,
        string? fallbackForRouteId,
        CancellationToken cancellationToken)
    {
        var estimatedInputTokens = TriageTokenEstimator.EstimateMessages(request.Messages, request.Tools);
        var estimatedOutputTokens = TriageTokenEstimator.EstimateText(response.Content);
        var usageSource = response.Usage is { InputTokens: > 0, OutputTokens: > 0, TotalTokens: > 0 } ? "provider" : "estimate";
        var inputTokens = PositiveOrEstimate(response.Usage?.InputTokens, estimatedInputTokens);
        var outputTokens = PositiveOrEstimate(response.Usage?.OutputTokens, estimatedOutputTokens);
        var totalTokens = PositiveOrEstimate(response.Usage?.TotalTokens, inputTokens + outputTokens);

        var metadata = new ModelCallLedgerMetadata(
            context.CallKind,
            context.RouteId,
            response.Model,
            response.Provider,
            usageSource,
            inputTokens,
            outputTokens,
            totalTokens,
            (long)duration.TotalMilliseconds,
            response.ProposedToolCalls?.Count ?? 0,
            callId,
            Outcome: "success",
            ErrorCode: null,
            ReasoningTokens: response.Usage?.ReasoningTokens,
            FallbackForRouteId: fallbackForRouteId,
            ProviderId: ConfiguredProviderId(request));

        var accounting = new InvestigationModelCallAccounting(
            callId,
            context.Role,
            metadata,
            totalTokens);

        await ledgerAppender.AppendModelCallAccountingAsync(context.Job, accounting, cancellationToken);
        LogModelCallCompleted(
            logger,
            context.Job.Id,
            context.Role,
            metadata.RouteId,
            metadata.Kind,
            metadata.Provider,
            metadata.ProviderId ?? UnrecordedProviderId,
            metadata.Model,
            metadata.UsageSource,
            inputTokens,
            outputTokens,
            totalTokens,
            metadata.DurationMs,
            metadata.ProposedToolCallCount);

        return totalTokens;
    }

    /// <summary>
    /// Builds the accounting a failed call still owes the ledger, without writing it: the caller
    /// carries it on <see cref="InvestigationModelCallFailureException"/> so the recovery path
    /// decides when it is durable.
    /// </summary>
    public static InvestigationModelCallAccounting CreateFailureAccounting(
        TriageJobCallContext context,
        AiModelRequest request,
        Guid callId,
        AiModelException exception,
        TimeSpan duration,
        string? fallbackForRouteId)
    {
        var usageSource = exception.Usage is null ? "unknown" : "provider";
        var errorCode = ProviderErrorCodes.For(exception.FailureKind, exception);
        var metadata = new ModelCallLedgerMetadata(
            context.CallKind,
            context.RouteId,
            string.IsNullOrWhiteSpace(exception.ReturnedModel) ? request.Model : exception.ReturnedModel!,
            exception.Provider,
            usageSource,
            exception.Usage?.InputTokens,
            exception.Usage?.OutputTokens,
            exception.Usage?.TotalTokens,
            (long)duration.TotalMilliseconds,
            ProposedToolCallCount: 0,
            CallId: callId,
            Outcome: RuntimeTelemetryOutcome.Failed.ToString().ToLowerInvariant(),
            ErrorCode: errorCode,
            ReasoningTokens: exception.Usage?.ReasoningTokens,
            FallbackForRouteId: fallbackForRouteId,
            ProviderId: ConfiguredProviderId(request));
        return new InvestigationModelCallAccounting(
            callId,
            context.Role,
            metadata,
            exception.Usage?.TotalTokens);
    }

    private static int PositiveOrEstimate(int? reportedTokens, int estimatedTokens)
    {
        return reportedTokens is > 0 ? reportedTokens.Value : estimatedTokens;
    }

    /// <summary>
    /// The provider table entry the called route named, which is the identity cost accounting keys
    /// on. It is recorded beside the adapter identifier rather than instead of it, because a host
    /// can declare two providers with different endpoints, credentials and prices that one adapter
    /// answers under a single name.
    /// </summary>
    /// <remarks>
    /// A blank id is recorded as absent rather than as an empty payer: a reader must be able to
    /// tell a row that names its provider from one that does not.
    /// </remarks>
    private static string? ConfiguredProviderId(AiModelRequest request)
    {
        return string.IsNullOrWhiteSpace(request.ProviderId) ? null : request.ProviderId;
    }

    [LoggerMessage(
        EventId = 3201,
        Level = LogLevel.Information,
        Message = "Model call for triage job {JobId} role {Role} route {RouteId} kind {CallKind} completed on {Provider}/{ProviderId}/{Model} with {UsageSource} usage {InputTokens}/{OutputTokens}/{TotalTokens} tokens in {DurationMs}ms proposing {ProposedToolCallCount} tool calls.")]
    private static partial void LogModelCallCompleted(
        ILogger logger,
        Guid jobId,
        string? role,
        string routeId,
        string callKind,
        string provider,
        string providerId,
        string model,
        string usageSource,
        int inputTokens,
        int outputTokens,
        int totalTokens,
        long durationMs,
        int proposedToolCallCount);
}
