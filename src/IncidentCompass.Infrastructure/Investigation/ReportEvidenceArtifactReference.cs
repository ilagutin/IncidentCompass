namespace IncidentCompass.Infrastructure.Investigation;

/// <summary>
/// The shape of the <c>referenceId</c> a report cites: a triage artifact id, optionally written with
/// the <c>artifact:</c> prefix the tool output uses. Two readers parse it - the grounder that
/// validates a citation and rejects an unparseable one, and the redaction-marker read that only
/// wants the ids it can resolve - so the rule lives in one place instead of drifting between them.
/// </summary>
internal static class ReportEvidenceArtifactReference
{
    private const string Prefix = "artifact:";

    public static bool TryParse(string referenceId, out Guid artifactId)
    {
        var normalized = referenceId.StartsWith(Prefix, StringComparison.Ordinal)
            ? referenceId[Prefix.Length..]
            : referenceId;
        return Guid.TryParse(normalized, out artifactId);
    }
}
