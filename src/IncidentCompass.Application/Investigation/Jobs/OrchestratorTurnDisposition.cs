namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// What one orchestrator turn did. Every way a turn can end is named here, so the investigation loop
/// reads as one named outcome per turn instead of several indistinguishable <c>continue</c> exits.
/// Only <see cref="ReportPublished"/> ends the investigation; every other disposition means the loop
/// takes another turn if the bounded turn allowance still has room.
/// </summary>
internal enum OrchestratorTurnDisposition
{
    /// <summary>The turn proposed no tool call and was reprompted to call exactly one.</summary>
    NoToolCallReprompted,

    /// <summary>A worker role ran to completion and its result was appended to the transcript.</summary>
    Delegated,

    /// <summary>The proposed delegate call was invalid and was reprompted for a corrected call.</summary>
    DelegateRepromptIssued,

    /// <summary>
    /// The proposed delegate repeated an equivalent delegate that had already returned the same worker
    /// output past <c>Orchestrator.Budget.MaxEquivalentCalls</c>; it was refused without running a
    /// worker and without charging a reprompt.
    /// </summary>
    DelegateRefusedAsRepeated,

    /// <summary>The grounded report was published; the investigation is finished.</summary>
    ReportPublished,

    /// <summary>The proposed report failed validation and was reprompted for a corrected report.</summary>
    PublishRepromptIssued,

    /// <summary>The turn proposed a tool outside the orchestrator surface and was reprompted.</summary>
    UnknownToolReprompted
}
