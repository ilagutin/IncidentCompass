using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Resilience;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// The one tool-less diagnostic model call made when an investigation stops making progress.
/// </summary>
/// <remarks>
/// <para>
/// It runs on the orchestrator route through <see cref="InvestigationModelCaller"/>, under the call
/// kind <see cref="TriageModelCallKinds.Recovery"/>, so admission, the token and duration budgets,
/// the provider limits, route fail-over and <c>ModelCall</c> accounting are the ones every other
/// investigation call gets. It is offered no tools, its messages are only the recovery instructions
/// and the backend progress summary, and nothing it returns is executed: its text is handed back to
/// the orchestrator as a suggestion. It cannot trigger another recovery, because only the
/// orchestrator loop counts turns and this call is not a turn.
/// </para>
/// <para>
/// <b>How it can end.</b>
/// A failure the next attempt could get past (an outage, a generation timeout, a transport failure, a
/// host that needs configuration, an unknown provider failure) propagates, so the job runner applies
/// the disposition it applies to any investigation call: an outage delays the job without spending an
/// attempt and feeds the outage pause. A failure that would repeat for the same request (a rejected
/// request, an output limit, an invalid response, an ambiguous interruption, a client contract breach)
/// has its accounting written here and comes back as <see cref="InvestigationRecoveryOutcome.Failed"/>.
/// A token-budget or context-window refusal before dispatch comes back as
/// <see cref="InvestigationRecoveryOutcome.NotAdmitted"/>. The attempt duration ceiling, cancellation,
/// a governance denial and an answered call whose accounting could not be written propagate.
/// </para>
/// </remarks>
internal sealed class InvestigationRecoveryCall(InvestigationModelCaller modelCaller, TriageLedgerAppender ledgerAppender)
{
    /// <summary>The bound, in UTF-16 code units, on the suggestion text that reaches the orchestrator.</summary>
    internal const int MaxSuggestionLength = 2000;

    internal const string EmptySuggestion = "The recovery review returned no suggestion.";

    public async Task<InvestigationRecoveryOutcome> RunAsync(
        TriageJob job,
        TriageConfiguration configuration,
        DateTimeOffset attemptStartedAtUtc,
        InvestigationProgressTracker progress,
        CancellationToken cancellationToken)
    {
        var routeId = configuration.Orchestrator.RouteId;
        if (!configuration.Routes.TryGetValue(routeId, out var route))
        {
            throw new TriageGovernanceDeniedException(
                TriageGovernanceDeniedException.OrchestratorRouteMissingCode,
                "Orchestrator route '" + routeId + "' is not a configured route in this triage configuration.");
        }

        var messages = new List<AiChatMessage>
        {
            new(AiMessageRole.System, InvestigationRecoveryInstructions.Resolve(configuration.Orchestrator)),
            new(AiMessageRole.User, InvestigationProgressSummaryBuilder.Build(job, progress))
        };
        try
        {
            var response = await modelCaller.CompleteAsync(
                new TriageJobCallContext(job, configuration, attemptStartedAtUtc, routeId, TriageModelCallKinds.Recovery),
                route,
                messages,
                tools: null,
                cancellationToken);
            var text = string.IsNullOrWhiteSpace(response.Content) ? EmptySuggestion : response.Content.Trim();
            return InvestigationRecoveryOutcome.Suggested(TruncateOnRuneBoundary(text, MaxSuggestionLength));
        }
        catch (TriageBudgetExhaustedException refused) when (
            refused.ErrorCode is TriageBudgetExhaustedException.MaxTokensReachedCode or
                TriageBudgetExhaustedException.ContextWindowExceededCode)
        {
            return InvestigationRecoveryOutcome.NotAdmitted(refused.ErrorCode);
        }
        catch (InvestigationModelCallFailureException failure) when (
            !failure.Accounting.ProviderAnswered && WouldRepeat(failure))
        {
            // The spend already happened, so a shutdown is not a reason to lose its record.
            await ledgerAppender.AppendModelCallAccountingAsync(job, failure.Accounting, CancellationToken.None);
            return InvestigationRecoveryOutcome.Failed(failure.Accounting.Metadata.ErrorCode);
        }
    }

    /// <summary>
    /// Cuts <paramref name="text"/> to at most <paramref name="maxLength"/> code units without splitting
    /// a surrogate pair.
    /// </summary>
    internal static string TruncateOnRuneBoundary(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..cut];
    }

    private static bool WouldRepeat(InvestigationModelCallFailureException failure) =>
        string.Equals(failure.Accounting.Metadata.ErrorCode, AiModelClientContractBreach.ErrorCode, StringComparison.Ordinal) ||
        ProviderOutageExceptionClassifier.FindFailureKind(failure) is ProviderFailureKind.RejectedRequest or
            ProviderFailureKind.OutputLimitReached or
            ProviderFailureKind.InvalidResponse or
            ProviderFailureKind.AmbiguousInterruption;
}
