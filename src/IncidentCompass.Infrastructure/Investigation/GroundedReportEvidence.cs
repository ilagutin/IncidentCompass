namespace IncidentCompass.Infrastructure.Investigation;

/// <summary>
/// One resolved citation, as the publication transaction sees it after grounding.
/// </summary>
/// <remarks>
/// <para>
/// <c>RetrievalConfidence</c> is the <c>retrievalConfidence</c> band <c>MemorySearchTool</c> wrote
/// onto the cited artifact payload. It is null for a citation that is not memory-backed, whose
/// payload shape carries no band, and for a memory payload written before the band was read here.
/// </para>
/// <para>
/// <c>IsMemoryBacked</c> is whether the citation rests on incident memory at all, which is what the
/// <c>KnownIncident</c> confirmation rule is scoped by. It is true for a retrieved item that
/// resolved to a memory item and for the durable <c>ToolResult</c> of a <c>memory_search</c> call,
/// and false for everything else, including a ticket-search or source-lookup retrieved item. It is
/// computed where the artifact kind and domain reference are in hand rather than re-derived from
/// <c>MemoryItemId</c>, because those two are not the same question.
/// </para>
/// </remarks>
internal sealed record GroundedReportEvidence(
    Guid ArtifactId,
    string Kind,
    string Reference,
    string? Quote,
    double? Score,
    Guid? MemoryItemId,
    string? DocumentationStatus,
    string? RetrievalConfidence,
    bool IsMemoryBacked);
