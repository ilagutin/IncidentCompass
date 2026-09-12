using System.Text;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Runs one bounded remediation pass: names the base, asks a model for a unified diff, applies it to
/// a disposable copy of that base, and records what it produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>The model call goes through <see cref="InvestigationModelCaller" />, not beside it.</b> That
/// type owns admission against the attempt budget, the authoritative provider deadline, durable
/// <c>ModelCall</c> accounting and the one declared fail-over hop. A second path would have to
/// reimplement all four, and each one would be wrong in a way that is invisible until it matters: an
/// unaccounted call is a cost the rollup cannot see, a second deadline is a configured bound
/// silently doubled, and a second admission point is a budget a remediation pass can walk around. It
/// is reached with its own <see cref="TriageModelCallKinds.Remediation" /> kind, so cost roll-ups
/// and logs can separate remediation spend from investigation spend without separating the rails
/// that bound it. The wall clock is measured from when this pass started, since it is a separate
/// bounded operation from the attempt that produced the report; the token budget is deliberately
/// the job attempt's own, so one incident's total model spend stays one number.
/// </para>
/// <para>
/// <b>An answer that is not a patch is a refusal, and it is worth one correction.</b> The shape the
/// worker-output path already uses applies here for the same reason: the failure is almost always a
/// formatting mistake the model can fix when told which rule fired, and re-running the whole pass to
/// get a differently formatted answer costs far more than one turn. So the pass reprompts, bounded
/// by the configuration's own <c>MaxReprompts</c>, and the correction carries the closed refusal code
/// and nothing else, exactly as the worker correction carries safe diagnostics rather than exception
/// text. What it does not do is reprompt on a refusal a different answer cannot fix. A base that
/// moved, a filesystem error and an unconfigured host are the environment's, not the model's, and
/// the adapter says which is which rather than leaving this type to guess from code strings. When
/// the allowance runs out the pass refuses with the last code and writes nothing: there is no record
/// of a diff that did not apply, so no downstream reader can mistake a failed attempt for evidence.
/// </para>
/// <para>
/// <b>The base obligation is enforced here.</b> An insert-only hunk quotes no base line, so the patch
/// text binds a change to no particular tree. This pass names the base before the model is asked,
/// states it in the request, and hands it back to the port on the way in, where the copy that is
/// about to be changed must match it or the attempt is refused before the diff is parsed. The same
/// identity is what the record carries, so a later application of an approved diff has something to
/// compare against.
/// </para>
/// <para>
/// <b>What this returns and what it raises.</b> A refusal code is an outcome this pass decided: a
/// missing route, no source evidence, no configured release, a base that could not be named, an
/// answer that stayed wrong. A budget exhaustion or a provider failure is not, and both propagate as
/// the exceptions the investigation path already raises, because retry, dead-lettering and
/// disposition belong to whatever schedules a pass and not to the pass itself. Catching them here
/// would turn a job-level decision into a code with nobody left to act on it.
/// </para>
/// <para>
/// <b>Nothing here executes anything.</b> No process is started, and the produced record says so.
/// </para>
/// </remarks>
internal sealed partial class RemediationDiffRunner(
    InvestigationModelCaller modelCaller,
    IRemediationWorkspace workspace,
    IRemediationDiffRepository diffRepository,
    TriageLedgerAppender ledgerAppender,
    TimeProvider timeProvider,
    ILogger<RemediationDiffRunner>? logger = null)
{
    /// <summary>
    /// The ledger role a remediation model call and its corrections are recorded under. It is not a
    /// configured worker role and cannot be delegated to; it names the pass in the timeline.
    /// </summary>
    internal const string LedgerRole = "remediation";

    /// <summary>
    /// The classification opening every remediation reprompt rationale in the ledger, matching the
    /// worker path's convention so one appender bound covers both.
    /// </summary>
    internal const string RepromptRationalePrefix = "remediation_patch_reprompt: ";

    private readonly ILogger logger = logger ?? NullLogger<RemediationDiffRunner>.Instance;

    public async Task<RemediationDiffRunResult> RunAsync(
        RemediationRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Configuration.Routes.TryGetValue(request.RouteId, out var route))
        {
            return RemediationDiffRunResult.Refused(RemediationCodes.RouteMissing);
        }

        if (request.SourceEvidence.Count == 0)
        {
            return RemediationDiffRunResult.Refused(RemediationCodes.SourceEvidenceMissing);
        }

        if (!request.Configuration.CurrentReleases.TryGetValue(request.Fault.ServiceName, out var release) ||
            string.IsNullOrWhiteSpace(release))
        {
            return RemediationDiffRunResult.Refused(RemediationCodes.ReleaseUnavailable);
        }

        var target = new RemediationTarget(request.Fault.ServiceName, release);
        var baseResult = await workspace.IdentifyBaseAsync(target, cancellationToken);
        if (baseResult.TreeIdentity is null)
        {
            return RemediationDiffRunResult.Refused(baseResult.Code);
        }

        return await AskAndApplyAsync(request, route, target, baseResult.TreeIdentity, cancellationToken);
    }

    private async Task<RemediationDiffRunResult> AskAndApplyAsync(
        RemediationRequest request,
        TriageRouteSettings route,
        RemediationTarget target,
        string baseTreeIdentity,
        CancellationToken cancellationToken)
    {
        var messages = new List<AiChatMessage>
        {
            new(AiMessageRole.System, request.Instructions),
            new(AiMessageRole.User, RemediationPromptBuilder.BuildRequestPrompt(request, target, baseTreeIdentity))
        };

        var context = new TriageJobCallContext(
            request.Job,
            request.Configuration,
            request.StartedAtUtc,
            request.RouteId,
            TriageModelCallKinds.Remediation,
            LedgerRole);

        var maxReprompts = request.Configuration.Orchestrator.Budget.MaxReprompts;
        var lastCode = RemediationCodes.AnswerNotAPatch;
        for (var turn = 0; turn <= maxReprompts; turn++)
        {
            var response = await modelCaller.CompleteAsync(context, route, messages, tools: null, cancellationToken);
            var patch = RemediationPatchAnswer.Extract(response.Content);
            var applied = patch is null
                ? RemediationApplyResult.Refused(RemediationCodes.AnswerNotAPatch, answerCorrectable: true)
                : await workspace.ApplyAsync(
                    new RemediationApplyRequest(target, baseTreeIdentity, patch),
                    cancellationToken);

            if (applied.ResultTreeIdentity is not null)
            {
                return await RecordAsync(request, target, baseTreeIdentity, patch!, response, applied, cancellationToken);
            }

            lastCode = applied.Code;
            if (!applied.AnswerCorrectable || turn == maxReprompts)
            {
                break;
            }

            await RepromptAsync(request, messages, response.Content, applied.Code, turn + 1, maxReprompts, cancellationToken);
        }

        LogRemediationRefused(logger, request.Job.Id, request.Job.Attempt, request.ReportId, lastCode);
        return RemediationDiffRunResult.Refused(lastCode);
    }

    /// <summary>
    /// Adds the refused answer and one correction to the transcript, and makes the correction
    /// durably visible as a bounded <c>BudgetEvent</c> the way a worker correction is.
    /// </summary>
    private async Task RepromptAsync(
        RemediationRequest request,
        List<AiChatMessage> messages,
        string previousAnswer,
        string code,
        int reprompt,
        int maxReprompts,
        CancellationToken cancellationToken)
    {
        LogRemediationReprompted(logger, request.Job.Id, request.Job.Attempt, reprompt, maxReprompts, code);
        await ledgerAppender.AppendRepromptBudgetEventAsync(
            request.Job,
            LedgerRole,
            RepromptRationalePrefix,
            code,
            cancellationToken);
        messages.Add(new AiChatMessage(AiMessageRole.Assistant, previousAnswer));
        messages.Add(new AiChatMessage(AiMessageRole.User, RemediationPromptBuilder.BuildCorrectionPrompt(code)));
    }

    /// <summary>
    /// Persists the produced diff. A record that could not be made durable is not a produced diff,
    /// so a persistence failure propagates rather than being reported as success with nothing behind
    /// it.
    /// </summary>
    private async Task<RemediationDiffRunResult> RecordAsync(
        RemediationRequest request,
        RemediationTarget target,
        string baseTreeIdentity,
        string patch,
        AiModelResponse response,
        RemediationApplyResult applied,
        CancellationToken cancellationToken)
    {
        var diff = new RemediationDiff(
            Guid.NewGuid(),
            request.Fault.TenantId,
            request.ReportId,
            request.Job.Id,
            request.Job.Attempt,
            target.ServiceName,
            target.Release,
            baseTreeIdentity,
            applied.ResultTreeIdentity!,
            applied.FilesChanged,
            Encoding.UTF8.GetByteCount(patch),
            patch,
            request.RouteId,
            response.Model,
            applied.Code,
            timeProvider.GetUtcNow());

        await diffRepository.AddAsync(diff, cancellationToken);
        LogRemediationProduced(
            logger,
            request.Job.Id,
            request.Job.Attempt,
            request.ReportId,
            diff.Id,
            diff.FilesChanged,
            diff.PatchBytes);
        return RemediationDiffRunResult.Produced(diff);
    }

    [LoggerMessage(
        EventId = 3801,
        Level = LogLevel.Information,
        Message = "Remediation pass for triage job {JobId} attempt {Attempt} report {ReportId} produced diff {DiffId} touching {FilesChanged} files in {PatchBytes} bytes. No test was executed.")]
    private static partial void LogRemediationProduced(
        ILogger logger,
        Guid jobId,
        int attempt,
        Guid reportId,
        Guid diffId,
        int filesChanged,
        int patchBytes);

    [LoggerMessage(
        EventId = 3802,
        Level = LogLevel.Warning,
        Message = "Remediation pass for triage job {JobId} attempt {Attempt} was reprompted after the backend refused the answer ({Reprompt}/{MaxReprompts}): {Outcome}")]
    private static partial void LogRemediationReprompted(
        ILogger logger,
        Guid jobId,
        int attempt,
        int reprompt,
        int maxReprompts,
        string outcome);

    [LoggerMessage(
        EventId = 3803,
        Level = LogLevel.Warning,
        Message = "Remediation pass for triage job {JobId} attempt {Attempt} report {ReportId} produced no diff: {Outcome}")]
    private static partial void LogRemediationRefused(
        ILogger logger,
        Guid jobId,
        int attempt,
        Guid reportId,
        string outcome);
}
