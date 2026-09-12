namespace IncidentCompass.Domain.Incidents;

// The citable subset used for grounding is {TriggerSignal, NeighborSet, PriorReport, RecurrenceState,
// RetrievedItem, ToolResult}. WorkerOutput is written as attempt-level working state and the
// grounding rules keep it non-citable.
public enum ArtifactKind
{
    TriggerSignal,
    NeighborSet,
    PriorReport,
    RecurrenceState,
    RetrievedItem,
    ToolResult,
    WorkerOutput,
    ProposedAction,
    ActionResult
}
