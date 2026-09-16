namespace IncidentCompass.Application.Investigation.Reports;

/// <summary>Why the backend ended a stalled investigation with its own report.</summary>
internal enum NoProgressTerminationReason
{
    /// <summary>Turns stalled past the window and every recovery was already used or failed.</summary>
    NoRecoveryLeft,

    /// <summary>A recovery was left, but the remaining turns or worker budget could not hold another window.</summary>
    NoWindowLeft,

    /// <summary>The recovery call was refused before dispatch because the token budget or context window had no room.</summary>
    RecoveryNotAdmitted,

    /// <summary>The orchestrator turn limit was reached while a detected stall was still open.</summary>
    TurnLimitDuringStall,

    /// <summary>The attempt worker budget was reached while a detected stall was still open.</summary>
    WorkerBudgetDuringStall
}
