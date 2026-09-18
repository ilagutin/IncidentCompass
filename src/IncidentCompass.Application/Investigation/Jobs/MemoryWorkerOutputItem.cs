namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// One retrieved document as the orchestrator receives it back from the memory worker.
/// <para>
/// <see cref="DocumentationStatus"/> is the backend's own label, assigned by
/// <c>MemoryDocumentationStatusEvaluator</c> inside <c>memory_search</c> and copied verbatim by the
/// worker. It is carried here because the orchestrator has to report <c>documentationFit</c>, which
/// the backend derives from exactly these labels over the documents the report cites: dropping it
/// left the orchestrator asked for a value whose only input it was never shown. It stays nullable
/// because a worker may legitimately omit it, and an absent label counts as no label rather than as
/// a current document.
/// </para>
/// <para>
/// <see cref="RetrievalConfidence"/> is carried for the same reason and is the same kind of value: a
/// band <c>memory_search</c> itself assigned, copied verbatim. The publication transaction refuses a
/// <c>KnownIncident</c> report whose every memory citation was admitted as related only, and without
/// this field the orchestrator was asked to satisfy a rule whose input it was never shown, so it
/// could only learn of the rule by spending its one correction turn on being refused.
/// </para>
/// <para>
/// Handing the model a governance-relevant field forges nothing. The backend rule reads the band off
/// the stored artifact payload <c>memory_search</c> wrote, never off the worker's copy, so a worker
/// that alters or invents a value here changes what it tells the orchestrator and nothing about what
/// the report is measured against.
/// </para>
/// </summary>
internal sealed record MemoryWorkerOutputItem(
    string ArtifactId,
    string Title,
    string Quote,
    double? Score,
    string? DocumentationStatus,
    string? RetrievalConfidence);
