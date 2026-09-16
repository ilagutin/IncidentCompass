using System.Globalization;
using System.Text;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Builds the user message of the recovery call from the progress record alone.
/// </summary>
/// <remarks>
/// Everything in it is a count, a configured role or tool name, the candidate classification label
/// an analysis output named (checked against the closed classification vocabulary) or the fixed task
/// framing. It holds no tool output, no argument, no delegated task text and no incident context,
/// so the recovery call sees nothing the orchestrator had not already seen and nothing a model
/// authored except one vocabulary label.
/// </remarks>
internal static class InvestigationProgressSummaryBuilder
{
    /// <summary>The first line of every summary.</summary>
    internal const string Header = "Backend progress summary of this investigation attempt. It contains no tool output.";

    public static string Build(TriageJob job, InvestigationProgressTracker progress)
    {
        var activity = progress.Activity;
        var classification = progress.CandidateClassification is { } candidate &&
            TriageClassificationVocabulary.Contains(candidate)
                ? candidate
                : "none";
        var builder = new StringBuilder();
        builder.AppendLine(Header);
        AppendLine(builder, "Job", job.Id.ToString());
        AppendLine(builder, "Attempt", Count(job.Attempt));
        AppendLine(builder, "Orchestrator turns completed", Count(activity.TurnsCompleted));
        AppendLine(builder, "Consecutive turns without progress", Count(progress.TurnsWithoutProgress));
        AppendLine(builder, "Delegations run", Count(activity.DelegationsRun));
        AppendLine(builder, "Roles delegated to", activity.DelegatedRoles.Count == 0 ? "none" : string.Join(", ", activity.DelegatedRoles));
        AppendLine(builder, "Worker tool calls executed", Count(activity.ToolCallsExecuted));
        AppendLine(builder, "Calls refused as repeats without new evidence", activity.RefusalsByCall.Count == 0
            ? "none"
            : string.Join(", ", activity.RefusalsByCall.Select(static pair => pair.Key + " x" + Count(pair.Value))));
        AppendLine(builder, "Workers stopped for repeating", Count(activity.WorkersStopped));
        AppendLine(builder, "Distinct evidence items", Count(progress.EvidenceCount));
        AppendLine(builder, "Current candidate classification", classification);
        builder.Append("Task the orchestrator was given: ").AppendLine(TriageInvestigationPromptBuilder.OrchestratorTaskInstruction);
        return builder.ToString();
    }

    private static void AppendLine(StringBuilder builder, string label, string value) =>
        builder.Append(label).Append(": ").AppendLine(value);

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
