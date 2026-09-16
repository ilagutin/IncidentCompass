using System.Globalization;
using System.Text.Json;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Governance;
using IncidentCompass.Domain.Incidents.Statuses;
using Microsoft.Extensions.Logging;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Runs one approved immediate worker tool under its execution limits and turns the ways it can end
/// into outcomes the ledger and the worker can act on. Three things bound the call: the host token
/// (shutdown or a lost lease), the tool's own <see cref="TriageToolSettings.TimeoutSeconds"/>, and
/// the time left before the attempt duration ceiling, resolved by
/// <see cref="TriageAttemptBudgetGate.ResolveRemainingAttemptDuration"/>. Both timers run on the
/// injected <see cref="TimeProvider"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The host token wins over both timers and propagates unchanged, with no <c>ToolResult</c>.</item>
/// <item>The attempt ceiling ends the attempt with the same wall-clock budget code a model call gets.</item>
/// <item>The tool's own limit becomes a <c>Failed</c> result with <c>tool_execution_timeout</c>. Immediate
/// tools are read-only, so no side effect is left uncertain, and the worker receives the ordinary
/// tool-failure message and can continue with an honest limitation.</item>
/// <item>Any other exception writes a <c>Failed</c> <c>ToolResult</c> with <c>tool_execution_failed</c>
/// naming the tool, then rethrows, so retry classification is unchanged.</item>
/// </list>
/// The outcome is chosen by which bound fired, in that order, not by the exception the tool threw.
/// Waiting stops as soon as a bound fires even when the tool ignores its token; the abandoned task
/// may keep running in the background until it returns, and its eventual exception is observed.
/// The logger is the executor's own instance, so these events keep its category. Logs carry the
/// tool id, the limit and an exception type only; never arguments or tool output.
/// </remarks>
internal sealed partial class WorkerToolExecutionLimiter(
    TriageLedgerAppender ledgerAppender,
    TimeProvider timeProvider,
    ILogger logger)
{
    public const string TimeoutErrorCode = "tool_execution_timeout";

    public const string FailedErrorCode = "tool_execution_failed";

    public async Task<ToolExecutionResult> ExecuteAsync(
        IImmediateAgentTool tool,
        AgentToolExecutionContext context,
        JsonElement sanitizedArguments,
        DateTimeOffset attemptStartedAtUtc,
        CancellationToken cancellationToken)
    {
        var job = context.Job;
        var toolName = context.ToolName;
        var toolLimit = ResolveToolLimit(context.Configuration, toolName);
        using var toolTimer = CreateTimer(toolLimit);
        using var attemptTimer = TriageAttemptBudgetGate.ResolveRemainingAttemptDuration(
            context.Configuration.Orchestrator.Budget, attemptStartedAtUtc, timeProvider) is { } remaining
            ? CreateTimer(remaining)
            : null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            toolTimer.Token,
            attemptTimer?.Token ?? CancellationToken.None);
        Task<ToolExecutionResult>? execution = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            execution = tool.ExecuteAsync(context, sanitizedArguments, linked.Token);

            // Waiting stops when any bound fires, whether or not the tool observes its token. A tool
            // that ignores cancellation is abandoned rather than awaited: it is read-only, so the
            // attempt can move on while it runs to completion in the background.
            return await execution.WaitAsync(linked.Token);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            ObserveAbandoned(execution);

            // Classified by which bound fired, not by the exception type, so a tool that turns its
            // cancellation into some other exception still ends as the bound that stopped it. The
            // host wins, then the attempt ceiling (it ends the attempt, so it outranks a per-tool
            // timeout that fired in the same instant), then the tool's own limit.
            if (cancellationToken.IsCancellationRequested)
            {
                if (exception is OperationCanceledException)
                {
                    throw;
                }

                throw new OperationCanceledException(
                    "The worker tool call was cancelled by the host.", exception, cancellationToken);
            }

            // The clock is checked as well as the timer: when both limits fall due at the same
            // instant, the per-tool timer can be the one whose callback ran first.
            if (attemptTimer?.IsCancellationRequested == true ||
                TriageAttemptBudgetGate.ResolveRemainingAttemptDuration(
                    context.Configuration.Orchestrator.Budget, attemptStartedAtUtc, timeProvider) <= TimeSpan.Zero)
            {
                throw await AttemptCeilingReachedAsync(context);
            }

            if (toolTimer.IsCancellationRequested)
            {
                return TimedOut(context, toolLimit);
            }

            await RecordThrownAsync(context, exception, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Keeps a tool task that was stopped being awaited from surfacing later as an unobserved task
    /// exception. It only reads the exception; nothing about it is logged.
    /// </summary>
    private static void ObserveAbandoned(Task<ToolExecutionResult>? execution)
    {
        if (execution is null || execution.IsCompletedSuccessfully)
        {
            return;
        }

        _ = execution.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<TriageBudgetExhaustedException> AttemptCeilingReachedAsync(AgentToolExecutionContext context)
    {
        var job = context.Job;
        LogWorkerToolAttemptCeilingReached(logger, job.Id, job.Attempt, context.RoleName, context.ToolName);
        try
        {
            await ledgerAppender.AppendBudgetEventAsync(
                job,
                "wall_clock_limit_reached: tool call " + context.ToolName +
                " exceeded remaining attempt wall-clock budget.",
                tokensDelta: null,
                workersDelta: null,
                cancellationToken: CancellationToken.None);
        }
        catch (Exception appendException) when (appendException is not (OutOfMemoryException or StackOverflowException))
        {
            // The budget exhaustion is what the attempt is classified by, so a failed audit append is
            // logged and must not replace it.
            LogWorkerToolBudgetEventNotRecorded(
                logger, job.Id, job.Attempt, context.RoleName, context.ToolName, appendException.GetType().Name);
        }

        return new TriageBudgetExhaustedException(
            TriageBudgetExhaustedException.WallClockReachedDuringCallCode,
            "The triage attempt exceeded " +
            context.Configuration.Orchestrator.Budget.ResolveAttemptDurationSettingName() +
            " during a tool call.");
    }

    private ToolExecutionResult TimedOut(AgentToolExecutionContext context, TimeSpan toolLimit)
    {
        var job = context.Job;
        var limitSeconds = (long)toolLimit.TotalSeconds;
        LogWorkerToolTimedOut(logger, job.Id, job.Attempt, context.RoleName, context.ToolName, limitSeconds);

        // The executor records a failed result as "<status>: <message>", so the message leads with
        // the code: the durable ToolResult row then names the code, the tool and the limit.
        return new ToolExecutionResult(
            ToolExecutionStatus.Failed,
            default,
            TimeoutErrorCode,
            TimeoutErrorCode + ": Tool " + context.ToolName + " exceeded its " +
            limitSeconds.ToString(CultureInfo.InvariantCulture) +
            "-second execution limit and was cancelled; it returned no result.");
    }

    private static TimeSpan ResolveToolLimit(TriageConfiguration configuration, string toolName) =>
        configuration.Tools.TryGetValue(toolName, out var settings)
            ? settings.ResolveImmediateTimeout()
            : TimeSpan.FromSeconds(TriageToolSettings.DefaultImmediateTimeoutSeconds);

    private CancellationTokenSource CreateTimer(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            var expired = new CancellationTokenSource();
            expired.Cancel();
            return expired;
        }

        return new CancellationTokenSource(delay, timeProvider);
    }

    /// <summary>
    /// Names the tool in the ledger before the exception leaves. The append is best-effort: when it
    /// fails too, the tool's own exception is what propagates, because that is what the attempt is
    /// classified by.
    /// </summary>
    private async Task RecordThrownAsync(
        AgentToolExecutionContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var job = context.Job;
        LogWorkerToolThrew(logger, job.Id, job.Attempt, context.RoleName, context.ToolName, exception.GetType().Name);
        try
        {
            await ledgerAppender.AppendToolResultAsync(
                job,
                context.RoleName,
                context.ToolName,
                TriageLedgerToolStatus.Failed,
                ToolExecutionStatus.Failed + ": " + FailedErrorCode + ": Tool " + context.ToolName +
                " failed with " + exception.GetType().Name + " before returning a result.",
                payloadRef: null,
                cancellationToken);
        }
        catch (Exception appendException) when (appendException is not (OutOfMemoryException or StackOverflowException))
        {
            LogWorkerToolFailureNotRecorded(
                logger, job.Id, job.Attempt, context.RoleName, context.ToolName, appendException.GetType().Name);
        }
    }

    [LoggerMessage(
        EventId = 3305,
        Level = LogLevel.Warning,
        Message = "Worker tool call {ToolName} for role {Role} on triage job {JobId} attempt {Attempt} exceeded its {ToolTimeoutSeconds}-second execution limit and was cancelled.")]
    private static partial void LogWorkerToolTimedOut(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName,
        long toolTimeoutSeconds);

    [LoggerMessage(
        EventId = 3306,
        Level = LogLevel.Warning,
        Message = "Worker tool call {ToolName} for role {Role} on triage job {JobId} attempt {Attempt} was cancelled because the attempt wall-clock budget ran out.")]
    private static partial void LogWorkerToolAttemptCeilingReached(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName);

    [LoggerMessage(
        EventId = 3307,
        Level = LogLevel.Warning,
        Message = "Worker tool call {ToolName} for role {Role} on triage job {JobId} attempt {Attempt} threw {ExceptionType}.")]
    private static partial void LogWorkerToolThrew(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName,
        string exceptionType);

    [LoggerMessage(
        EventId = 3308,
        Level = LogLevel.Warning,
        Message = "Worker tool call {ToolName} for role {Role} on triage job {JobId} attempt {Attempt} threw, and its failed tool result could not be recorded ({ExceptionType}).")]
    private static partial void LogWorkerToolFailureNotRecorded(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName,
        string exceptionType);

    [LoggerMessage(
        EventId = 3309,
        Level = LogLevel.Warning,
        Message = "Worker tool call {ToolName} for role {Role} on triage job {JobId} attempt {Attempt} reached the attempt wall-clock budget, but its budget event could not be recorded ({ExceptionType}).")]
    private static partial void LogWorkerToolBudgetEventNotRecorded(
        ILogger logger,
        Guid jobId,
        int attempt,
        string role,
        string toolName,
        string exceptionType);
}
