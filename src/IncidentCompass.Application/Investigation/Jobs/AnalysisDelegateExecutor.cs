using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Ledger;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Artifacts;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Domain.Incidents;
using IncidentCompass.Domain.Incidents.Statuses;

namespace IncidentCompass.Application.Investigation.Jobs;

internal sealed class AnalysisDelegateExecutor(
    ITriageArtifactRepository artifactRepository,
    TriageLedgerAppender ledgerAppender,
    ITriageLedgerReader ledgerReader,
    WorkerRoleRunner workerRoleRunner,
    TimeProvider timeProvider)
{
    /// <summary>
    /// The diagnostic an unknown role is refused with. It is one fixed string and it does not name
    /// the value that was rejected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing model-authored is echoed.</b> <c>role</c> arrives in the tool call's arguments, so
    /// it is model text that a model steered by attacker-influenced incident data chose, of any
    /// length and any characters. The earlier form put that string back into the tool result
    /// verbatim, which made this the one place in the codebase that reflected untrusted text: the
    /// report path runs a closed allowlist through <c>OrchestratorRepromptDiagnostics</c> and the
    /// remediation path promises not to return a path, a line or a byte of a diff. Naming the role
    /// also buys nothing, because the delegate tool schema already hands the model the closed enum of
    /// configured role names, so the model is being told a value is not in a list it was given.
    /// </para>
    /// <para>
    /// <b>It is a correctable refusal, not a silent early return.</b> Leaving through
    /// <see cref="DelegateToolCallValidationException" /> puts an unknown role on the same path as a
    /// delegate call missing its <c>role</c> string, which is the same kind of mistake: a tool call
    /// that did not honour a schema the model was given. That path charges the turn against the
    /// bounded reprompt allowance, writes the durable <c>orchestrator_reprompt:</c> budget event and
    /// log 3401, and fails the attempt closed under
    /// <c>TriageBudgetExhaustedException.OrchestratorRepromptLimitReachedCode</c> once the allowance
    /// is spent. The early return did none of that: it skipped the worker-budget check and every
    /// ledger append below, and the loop still classified the turn as delegated, so a model looping
    /// on an unknown role burned the whole turn allowance leaving nothing in the ledger to say why.
    /// </para>
    /// </remarks>
    internal const string UnknownRoleMessage =
        "delegate role is not one of the configured roles the delegate tool offers.";

    public async Task<string> ExecuteAsync(
        TriageJob job,
        TriageConfiguration configuration,
        TriageJobInvestigationContext context,
        AiToolCall toolCall,
        DateTimeOffset attemptStartedAtUtc,
        CancellationToken cancellationToken)
    {
        var (roleName, task) = ReadDelegateArguments(toolCall.Arguments);
        if (!configuration.Roles.TryGetValue(roleName, out var role))
        {
            throw CreateException(UnknownRoleMessage);
        }

        await EnsureWorkerBudgetAsync(job, configuration, roleName, cancellationToken);
        await ledgerAppender.AppendAsync(job, TriageLedgerEventType.Delegated, roleName, OrchestratorToolNames.Delegate, task, null, cancellationToken);
        await ledgerAppender.AppendBudgetEventAsync(
            job,
            "worker_started: accepted delegated worker for this attempt.",
            tokensDelta: null,
            workersDelta: 1,
            cancellationToken: cancellationToken);

        var workerContent = await workerRoleRunner.RunAsync(
            job,
            configuration,
            context,
            roleName,
            role,
            task,
            attemptStartedAtUtc,
            cancellationToken);
        var artifact = await InsertWorkerOutputArtifactAsync(job, configuration, roleName, workerContent, cancellationToken);
        // The delegate result is built from the stored payload, not from the worker's raw text: its
        // serialized form becomes an orchestrator model message and its rationale becomes ledger
        // state, so leaving it raw would hand both surfaces exactly what the artifact row hides.
        // Reading it back off the artifact also means the summary the orchestrator sees and the row
        // its artifactId points at are the same bytes.
        var delegateResult = WorkerDelegateResultFactory.Create(
            roleName, artifact.RedactedPayload.GetRawText(), artifact.Id);

        await ledgerAppender.AppendAsync(
            job,
            TriageLedgerEventType.WorkerCompleted,
            roleName,
            toolName: null,
            delegateResult.Rationale,
            $"artifact:{artifact.Id}",
            cancellationToken);

        return delegateResult.SerializedPayload;
    }

    private async Task EnsureWorkerBudgetAsync(
        TriageJob job,
        TriageConfiguration configuration,
        string roleName,
        CancellationToken cancellationToken)
    {
        var usage = await ledgerReader.ReadBudgetUsageAsync(job, cancellationToken);
        if (usage.WorkerCalls < configuration.Orchestrator.Budget.MaxWorkers)
        {
            return;
        }

        await ledgerAppender.AppendBudgetEventAsync(
            job,
            "max_workers_reached: attempt worker budget was already reached.",
            tokensDelta: null,
            workersDelta: null,
            cancellationToken: cancellationToken);
        throw new TriageBudgetExhaustedException(
            TriageBudgetExhaustedException.MaxWorkersReachedCode,
            "The triage attempt worker budget was reached before delegation.");
    }

    /// <summary>
    /// The worker's own output is model text that quotes the tool results it was given, so it takes
    /// the same redaction path as any tool artifact rather than a private one. It goes through
    /// <see cref="RedactedToolArtifactFactory"/> for that reason and so that this file is not a
    /// second place that constructs a <see cref="TriageArtifact"/> from unredacted text.
    /// <para>
    /// Order matters here: the role's output schema was validated against the raw text before this
    /// runs, and redaction can still change a value afterwards - it rewrites strings, and it replaces
    /// a value of any kind whose property name looks like a secret holder. No shipped role schema
    /// names such a property, and the parsers downstream read only <c>keyFacts</c>,
    /// <c>candidateClassification</c>, <c>needsDeeperContext</c>, <c>matched</c>, <c>items</c>,
    /// <c>artifactId</c>, <c>title</c>, <c>quote</c>, <c>score</c> and <c>documentationStatus</c>,
    /// none of which the denylist matches. A role schema that did
    /// name one would make the redacted document fail the delegate parse, which fails the attempt
    /// rather than leaking anything.
    /// </para>
    /// </summary>
    private async Task<TriageArtifact> InsertWorkerOutputArtifactAsync(
        TriageJob job,
        TriageConfiguration configuration,
        string roleName,
        string content,
        CancellationToken cancellationToken)
    {
        var payload = JsonNode.Parse(content) ?? new JsonObject { ["raw"] = content };
        var artifact = RedactedToolArtifactFactory.Create(
            job,
            new ToolArtifactDraft(ArtifactKind.WorkerOutput, $"worker:{roleName}", payload),
            configuration.Redaction,
            timeProvider.GetUtcNow());

        await artifactRepository.InsertAsync(artifact, cancellationToken);
        return artifact;
    }

    private static (string Role, string Task) ReadDelegateArguments(JsonElement arguments)
    {
        var root = JsonElementReader.RequireObject(
            arguments,
            "delegate arguments must be an object.",
            CreateException);
        var role = ReadDelegateString(root, "role");
        var task = ReadDelegateString(root, "task");
        return (role, task);
    }

    private static string ReadDelegateString(JsonElement arguments, string propertyName)
    {
        return JsonElementReader.ReadRequiredString(
            arguments,
            propertyName,
            $"delegate is missing string {propertyName}.",
            CreateException,
            trim: false);
    }

    private static Exception CreateException(string message)
    {
        return new DelegateToolCallValidationException(message);
    }
}
