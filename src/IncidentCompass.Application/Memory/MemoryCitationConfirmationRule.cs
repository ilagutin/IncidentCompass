using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;

namespace IncidentCompass.Application.Memory;

/// <summary>
/// The publication-time rule that keeps a <c>KnownIncident</c> classification off memory nothing
/// confirmed: a <c>Completed</c> report classified <c>KnownIncident</c> that cites at least one
/// memory-backed retrieved document must cite at least one whose band confirms the match.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is a backend rule.</b> A <c>low</c> item is real context and the model may read it, but
/// the consumer of a retrieved item is a model rather than a person who would notice the difference
/// between a document about this subsystem and a document about this failure. A <c>KnownIncident</c>
/// classification is the one that a ticket and a remediation diff follow from, so it is the one that
/// may not rest on a match the relevance judge declined to confirm. Before this rule only prompt
/// text asked for that care.
/// </para>
/// <para>
/// <b>Why it is scoped to memory-backed citations.</b> A ticket-search or source-lookup result is
/// also stored as a <c>RetrievedItem</c> artifact and reported under evidence kind
/// <c>RetrievedItem</c>, and a <c>KnownIncident</c> report grounded on one of those is legitimate
/// today. Their payloads have closed shapes that carry no band at all, so a rule phrased over the
/// evidence kind would refuse them. The caller therefore passes the bands of the citations that rest
/// on incident memory, and a report citing none of those passes an empty set and is untouched.
/// </para>
/// <para>
/// <b>What counts as resting on incident memory.</b> A retrieved item that resolved to a memory
/// item, and the durable <c>ToolResult</c> of a <c>memory_search</c> call, which holds the same
/// titles and quotes as the per-item artifacts of that call. The tool result has no band at any
/// level, so it is passed as a null entry and can never be what confirms: a <c>KnownIncident</c>
/// cannot rest on it alone. The grounder decides this, where the artifact kind and domain reference
/// are in hand.
/// </para>
/// <para>
/// <b>Why not the evidence-kind string.</b> A memory item of kind <c>known_incident</c> is reported
/// under the evidence kind <c>KnownIncident</c>, which is a different column from the report
/// classification and spelled the same. Discriminating on it would be a different rule, and would
/// wrongly exclude a confirmed <c>Runbook</c> or <c>Postmortem</c> citation.
/// </para>
/// </remarks>
internal static class MemoryCitationConfirmationRule
{
    /// <summary>
    /// The refusal handed back to the orchestrator. It is one fixed backend-authored sentence pair:
    /// no artifact id, no document title, no quote and no model-authored text can reach it, which is
    /// what lets <c>OrchestratorRepromptDiagnostics</c> allowlist it by exact match.
    /// </summary>
    public const string UnconfirmedMemoryCitationRefusal =
        "publish_report classification KnownIncident requires at least one cited retrieved document " +
        "that the backend confirmed describes this fault. Every retrieved document this report cites " +
        "was admitted as related only.";

    /// <summary>
    /// Whether the report must be refused. <paramref name="citedMemoryBands"/> is the
    /// <c>retrievalConfidence</c> of each cited memory-backed document, in any order, with a null
    /// entry for a payload that carries no band.
    /// </summary>
    public static bool RefusesUnconfirmedMemoryCitations(
        TriageReportStatus status,
        string classification,
        IReadOnlyCollection<string?> citedMemoryBands)
    {
        ArgumentNullException.ThrowIfNull(citedMemoryBands);
        if (status != TriageReportStatus.Completed ||
            !string.Equals(classification, TriageClassificationVocabulary.KnownIncident, StringComparison.Ordinal))
        {
            return false;
        }

        return citedMemoryBands.Count > 0 &&
            !citedMemoryBands.Any(MemoryRetrievalConfidence.ConfirmsMatch);
    }
}
