namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationEvidenceSnapshot(
    Guid EvidenceId,
    string Kind,
    Guid ArtifactId,
    string ArtifactKind,
    string? ArtifactDomainRef,
    string Reference,
    double? Score);
