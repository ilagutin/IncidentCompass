using IncidentCompass.Application.Investigation.Reports;

namespace IncidentCompass.Application.Investigation.Jobs;

internal static class OrchestratorRepromptDiagnostics
{
    internal const string DocumentationFitValueInvalid =
        "publish_report documentationFit must be Current, CurrentWithHistorical, StaleOnly, Missing, or MultipleCurrentDocuments.";
    internal const string UnknownReportValidationFailure =
        "publish_report failed backend validation.";

    private static readonly HashSet<string> KnownReportDiagnostics = CreateKnownReportDiagnostics();

    public static string ForReportValidation(TriageReportValidationException exception) =>
        KnownReportDiagnostics.Contains(exception.Message)
            ? exception.Message
            : UnknownReportValidationFailure;

    public static string ForDelegateValidation(DelegateToolCallValidationException exception) =>
        exception.Message switch
        {
            "delegate arguments must be an object." => exception.Message,
            "delegate is missing string role." => exception.Message,
            "delegate is missing string task." => exception.Message,
            _ => "delegate failed backend validation."
        };

    private static HashSet<string> CreateKnownReportDiagnostics()
    {
        var diagnostics = new HashSet<string>(StringComparer.Ordinal)
        {
            "A re-triage report must cite recurrence state evidence.",
            DocumentationFitValueInvalid,
            "publish_report evidence referenceId does not resolve to a citable artifact for this job attempt.",
            "publish_report evidence referenceId must be a triage artifact id.",
            "publish_report RetrievedItem evidence has an unsupported or invalid evidence shape.",
            "publish_report status must be Completed or InsufficientEvidence.",
            "publish_report confidence must be Low, Medium, or High.",
            "Completed reports must include at least one evidence item.",
            "publish_report classification is not supported.",
            "InsufficientEvidence reports must use classification Unknown.",
            "Completed reports must use a concrete non-Unknown classification.",
            "publish_report arguments must be an object.",
            "publish_report report_json must be an object.",
            "publish_report report must be an object.",
            "publish_report evidence must be an array.",
            "publish_report evidence items must be objects.",
            "publish_report quote must be a string.",
            "publish_report limitations must be an array of strings.",
            "publish_report limitations must contain only strings."
        };

        // A documentationFit mismatch is the one refusal that names a backend-derived value, so its
        // message is not one constant but a closed family of them. Every member is enumerated here,
        // which keeps this an exact-match allowlist: the set of strings that can pass is fixed at
        // startup and grows only when the enum does.
        foreach (var mismatch in DocumentationFitDiagnostics.AllMismatchMessages())
        {
            diagnostics.Add(mismatch);
        }

        foreach (var propertyName in new[]
        {
            "status",
            "classification",
            "confidence",
            "documentationFit",
            "summary",
            "recommendedNextAction",
            "referenceId"
        })
        {
            diagnostics.Add($"publish_report is missing string {propertyName}.");
            diagnostics.Add($"publish_report {propertyName} must be a string.");
        }

        return diagnostics;
    }
}
