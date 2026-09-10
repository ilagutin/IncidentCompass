namespace IncidentCompass.Application.Investigation.Reports.Get;

/// <remarks>
/// <c>ModelProvenance</c> carries the distinct models that answered during the job attempt that
/// produced this report. It is <see langword="null"/> for a report published before provenance was
/// recorded, which is deliberately not the same claim as an empty list: a report is immutable, so
/// those rows cannot be backfilled with what was never observed.
/// </remarks>
public sealed record TriageReportDetailsResponse(
    Guid Id,
    Guid FaultId,
    string Status,
    string Summary,
    string Classification,
    string Confidence,
    string DocumentationFit,
    bool? IsMassIssue,
    string RecommendedNextAction,
    IReadOnlyList<string> Limitations,
    string ConfigHash,
    DateTimeOffset CreatedAtUtc,
    Guid? SupersedesReportId,
    Guid? SupersededByReportId,
    bool IsLatestForFault,
    IReadOnlyList<TriageReportEvidenceResponse> Evidence,
    IReadOnlyList<TriageReportModelParticipant>? ModelProvenance = null);
