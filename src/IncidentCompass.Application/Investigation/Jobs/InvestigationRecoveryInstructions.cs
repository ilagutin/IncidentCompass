using IncidentCompass.Application.Intake.Configuration;

namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// The system instructions of the recovery call: the configured
/// <c>Orchestrator.RecoveryInstructions</c>, or <see cref="Default"/> when it is absent.
/// </summary>
/// <remarks>
/// The default lives in code rather than as an implicit reference to a file, because an implicit
/// reference would change the configuration hash of every configuration that does not set the key,
/// and a stored snapshot taken before the key existed must still rehydrate. The shipped
/// <c>config/instructions/recovery.md</c> is this text, byte for byte (a test pins it), so an operator
/// who wants to change it copies the file, edits it and sets the key.
/// </remarks>
internal static class InvestigationRecoveryInstructions
{
    public const string Default =
        "You review an incident investigation that has stopped making progress. You have no tools and you cannot run anything.\n" +
        "\n" +
        "The user message is a backend summary of the investigation so far: how many turns and calls it made, which roles it delegated to, which calls the backend refused because they repeated a call that had already returned the same result, how much distinct evidence it holds, its current candidate classification and the task it was given. It contains no tool output.\n" +
        "\n" +
        "Suggest one concrete next step the orchestrator can take with its own tools:\n" +
        "- delegate to a role with a different, specific task that could produce evidence the investigation does not have yet; or\n" +
        "- call publish_report with what it has, using status InsufficientEvidence and classification Unknown when the evidence does not support a conclusion.\n" +
        "\n" +
        "Do not suggest repeating a call the summary lists as refused. Do not invent evidence, artifact ids or results.\n" +
        "\n" +
        "Answer in at most five short sentences of plain text. Do not output JSON or tool calls.\n";

    public static string Resolve(OrchestratorSettings orchestrator) =>
        string.IsNullOrWhiteSpace(orchestrator.RecoveryInstructions) ? Default : orchestrator.RecoveryInstructions;
}
