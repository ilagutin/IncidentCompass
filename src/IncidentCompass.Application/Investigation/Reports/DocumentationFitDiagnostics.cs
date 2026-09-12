namespace IncidentCompass.Application.Investigation.Reports;

/// <summary>
/// The refusal a <c>documentationFit</c> mismatch hands back to the orchestrator.
/// <para>
/// The message names the value the backend derived. It can, because the message is a pure function
/// of one <see cref="DocumentationFitStatus"/>: the whole set of messages this type can ever produce
/// is <see cref="AllMismatchMessages"/>, five fixed strings that differ only by an enum name the
/// backend already publishes in the <c>publish_report</c> tool schema. No document title, quote,
/// artifact id, release marker or model-authored text can reach it, which is why
/// <c>OrchestratorRepromptDiagnostics</c> can allowlist every one of them by exact match and still
/// be a closed vocabulary.
/// </para>
/// <para>
/// Naming it is the point. Without it a correction turn tells the model that its value is wrong and
/// not what would be right, so the only move left is another guess over the same five values.
/// </para>
/// </summary>
internal static class DocumentationFitDiagnostics
{
    public static string Mismatch(DocumentationFitStatus derived) =>
        "publish_report documentationFit does not match backend-derived cited-document status. " +
        "The backend derived " + derived.ToString() + " from the retrieved documents this report cites.";

    public static IEnumerable<string> AllMismatchMessages() =>
        Enum.GetValues<DocumentationFitStatus>().Select(Mismatch);
}
