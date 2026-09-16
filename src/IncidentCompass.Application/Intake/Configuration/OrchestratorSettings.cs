namespace IncidentCompass.Application.Intake.Configuration;

/// <param name="Instructions">The orchestrator system instructions, usually a <c>ref:</c> to a file.</param>
/// <param name="RouteId">The chat route every orchestrator and recovery call uses.</param>
/// <param name="Tools">Exactly <c>delegate</c> and <c>publish_report</c>.</param>
/// <param name="Budget">The per-attempt budget.</param>
/// <param name="RecoveryInstructions">
/// Optional system instructions for the tool-less recovery call, resolved like
/// <paramref name="Instructions"/>. Absent means the built-in text that
/// <c>config/instructions/recovery.md</c> ships with, so a configuration that does not set it keeps its
/// hash.
/// </param>
public sealed record OrchestratorSettings(
    string Instructions,
    string RouteId,
    IReadOnlyCollection<string> Tools,
    OrchestratorBudgetSettings Budget,
    string? RecoveryInstructions = null);
