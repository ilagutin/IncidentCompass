using System.Security.Cryptography;
using System.Text;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Application.Governance.ActionApprovals;

/// <summary>
/// Claims and dispatches approved external actions. Each action runs under its tool's own limit,
/// <c>Tools.&lt;id&gt;.TimeoutSeconds</c>, or the Worker host's <c>AdapterTimeoutSeconds</c> when the tool
/// sets none. The limit is resolved from the current configuration before the claim, so the adapter
/// deadline and the claim's <c>dispatch_deadline_at</c> (limit plus the recovery grace) agree.
/// </summary>
/// <remarks>
/// A failed row says how far dispatch got. Anything that stops dispatch before the adapter is invoked,
/// cancellation included, is <c>dispatch_not_invoked</c> unless a more specific pre-invocation code
/// applies: nothing was sent, and the action is not re-dispatched. Once the adapter is invoked, a
/// timeout, a cancellation or a fault is <c>dispatch_outcome_unknown</c>, because the side effect may
/// have happened.
/// </remarks>
internal sealed partial class ApprovedActionDispatcher(
    IActionDispatchRepository repository,
    IExternalActionToolRegistry externalTools,
    IAgentToolRegistry toolRegistry,
    ITriageConfigurationRepository configurationRepository,
    TimeProvider timeProvider,
    ILogger<ApprovedActionDispatcher>? logger = null) : IApprovedActionDispatcher
{
    private readonly ILogger logger = logger ?? NullLogger<ApprovedActionDispatcher>.Instance;

    public const string NotInvokedCode = "dispatch_not_invoked";

    public const string OutcomeUnknownCode = "dispatch_outcome_unknown";

    private static readonly TimeSpan RecoveryGrace = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TerminalCommitBudget = TimeSpan.FromSeconds(5);

    public async Task SweepAsync(int batchSize, CancellationToken cancellationToken)
    {
        foreach (var candidate in await repository.FindExpiryCandidatesAsync(batchSize, cancellationToken))
        {
            await repository.TryExpireAsync(candidate.ActionId, cancellationToken);
        }

        foreach (var candidate in await repository.FindSupersededCandidatesAsync(batchSize, cancellationToken))
        {
            await repository.TryFailSupersededAsync(candidate.ActionId, cancellationToken);
        }

        foreach (var candidate in await repository.FindRecoveryCandidatesAsync(batchSize, cancellationToken))
        {
            await repository.TryFailOutcomeUnknownAsync(candidate.ActionId, cancellationToken);
        }
    }

    public Task<IReadOnlyList<ActionDispatchCandidate>> FindCandidatesAsync(
        int limit,
        CancellationToken cancellationToken) =>
        repository.FindCandidatesAsync(limit, cancellationToken);

    public async Task<ActionDispatchClaim?> TryClaimAsync(
        Guid actionId,
        string dispatchOwner,
        TimeSpan adapterTimeout,
        CancellationToken cancellationToken)
    {
        // Read before the claim transaction opens, so choosing the per-tool deadline adds no I/O to
        // it. When the configuration cannot be read the claim is taken with the host value, and
        // dispatch reads the configuration again under that same limit.
        TriageConfiguration? configuration = null;
        try
        {
            configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogClaimConfigurationUnavailable(
                logger, actionId, (long)adapterTimeout.TotalSeconds, exception.GetType().Name);
        }

        var claim = await repository.TryClaimAsync(
            actionId,
            dispatchOwner,
            toolId => ResolveAdapterTimeout(configuration, toolId, adapterTimeout) + RecoveryGrace,
            cancellationToken);
        return claim is null
            ? null
            : claim with { AdapterTimeout = ResolveAdapterTimeout(configuration, claim.Action.ToolId, adapterTimeout) };
    }

    public async Task DispatchAsync(
        ActionDispatchClaim claim,
        CancellationToken cancellationToken)
    {
        if (claim.AdapterTimeout is not { } limit)
        {
            throw new ArgumentException(
                "The claim carries no adapter limit; claim it through TryClaimAsync.", nameof(claim));
        }

        (ActionTerminalRequest? Terminal, IExternalActionTool? Tool) prepared;
        try
        {
            prepared = await PrepareAsync(claim, cancellationToken);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            prepared = (ActionDispatchTerminalFactory.Failure(
                claim, NotInvokedCode, "Dispatch stopped before the adapter was invoked."), null);
        }

        if (prepared.Terminal is not null)
        {
            await CompleteAsync(prepared.Terminal);
            return;
        }

        ActionTerminalRequest terminal;
        try
        {
            using var adapterTimer = new CancellationTokenSource(limit, timeProvider);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, adapterTimer.Token);
            var result = await prepared.Tool!.ExecuteAsync(
                claim.Action.Id,
                claim.Action.CanonicalPayload,
                deadline.Token);
            terminal = ActionDispatchTerminalFactory.FromAdapter(claim, result);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            terminal = ActionDispatchTerminalFactory.Failure(
                claim,
                OutcomeUnknownCode,
                "The external action outcome is unknown.");
        }

        await CompleteAsync(terminal);
    }

    /// <summary>
    /// Everything that must hold before the adapter is invoked. It either returns the terminal the
    /// action closes with, or the adapter to invoke. An exception, cancellation included, leaves
    /// through the caller as <see cref="NotInvokedCode"/>.
    /// </summary>
    private async Task<(ActionTerminalRequest? Terminal, IExternalActionTool? Tool)> PrepareAsync(
        ActionDispatchClaim claim,
        CancellationToken cancellationToken)
    {
        if (!externalTools.TryGet(claim.Action.ToolId, out var tool) ||
            !toolRegistry.TryGet(claim.Action.ToolId, out var descriptor))
        {
            return (ActionDispatchTerminalFactory.Failure(
                claim, "action_tool_unavailable", "External action tool is unavailable."), null);
        }

        TriageConfiguration configuration;
        try
        {
            configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return (ActionDispatchTerminalFactory.Failure(
                claim, "action_configuration_unavailable", "Current action policy is unavailable."), null);
        }

        var guard = ActionDispatchGuard.Evaluate(claim.Action, descriptor, configuration);
        if (guard.FailureCode is not null)
        {
            return (ActionDispatchTerminalFactory.Failure(
                claim, guard.FailureCode, "Current action policy rejected dispatch."), null);
        }

        if (guard.Simulate)
        {
            return (ActionDispatchTerminalFactory.DryRun(claim), null);
        }

        if (!BindingMatches(claim.Action.AdapterBindingFingerprint, tool.AdapterBindingFingerprint))
        {
            return (ActionDispatchTerminalFactory.Failure(
                claim, "adapter_binding_changed", "External action adapter binding changed."), null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (null, tool);
    }

    private static TimeSpan ResolveAdapterTimeout(
        TriageConfiguration? configuration,
        string toolId,
        TimeSpan hostDefault) =>
        configuration is not null && configuration.Tools.TryGetValue(toolId, out var settings)
            ? settings.ResolveActionTimeout(hostDefault)
            : hostDefault;

    private async Task CompleteAsync(ActionTerminalRequest terminal)
    {
        using var commitDeadline = new CancellationTokenSource(TerminalCommitBudget);
        await repository.CompleteAsync(terminal, commitDeadline.Token);
    }

    private static bool BindingMatches(string frozen, string current)
    {
        if (!ActionProposalValidator.IsLowerHexSha256(frozen) ||
            !ActionProposalValidator.IsLowerHexSha256(current))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(frozen),
            Encoding.ASCII.GetBytes(current));
    }

    [LoggerMessage(
        EventId = 3521,
        Level = LogLevel.Warning,
        Message = "Current triage configuration could not be read before claiming action {ActionId} ({ExceptionType}); the claim uses the host adapter limit of {AdapterTimeoutSeconds} seconds.")]
    private static partial void LogClaimConfigurationUnavailable(
        ILogger logger,
        Guid actionId,
        long adapterTimeoutSeconds,
        string exceptionType);
}
