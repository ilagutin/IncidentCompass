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
/// </summary>
internal sealed record MemoryWorkerOutputItem(
    string ArtifactId,
    string Title,
    string Quote,
    double? Score,
    string? DocumentationStatus);
