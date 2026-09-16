namespace IncidentCompass.Application.Investigation.Jobs;

/// <param name="Rationale">The worker's summary, recorded on the <c>WorkerCompleted</c> ledger entry.</param>
/// <param name="SerializedPayload">The delegate result the orchestrator receives.</param>
/// <param name="CandidateClassification">
/// The candidate classification an analysis output named, which feeds progress detection; absent for
/// a memory output.
/// </param>
internal sealed record WorkerDelegateResult(
    string Rationale,
    string SerializedPayload,
    string? CandidateClassification = null);
