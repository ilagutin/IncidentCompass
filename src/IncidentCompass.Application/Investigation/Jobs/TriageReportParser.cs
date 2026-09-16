using System.Text.Json;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Investigation.Reports;

namespace IncidentCompass.Application.Investigation.Jobs;

internal static class TriageReportParser
{
    internal const string ReportJsonWrapper = "report_json";
    internal const string ReportWrapper = "report";
    internal const int MaxSummaryLength = 4000;
    internal const int MaxRecommendedNextActionLength = 2000;
    internal const int MaxLimitationItems = 20;
    internal const int MaxLimitationLength = 1000;
    internal const int MaxEvidenceItems = 50;
    internal const int MaxQuoteLength = 1000;

    private static readonly HashSet<string> AllowedConfidence = new(["Low", "Medium", "High"], StringComparer.Ordinal);

    public static TriageReport Parse(JsonElement arguments)
    {
        var root = ResolveReportRoot(arguments);
        var statusName = ReadReportString(root, "status");
        if (!Enum.IsDefined(typeof(TriageReportStatus), statusName) ||
            !Enum.TryParse<TriageReportStatus>(statusName, ignoreCase: false, out var status) ||
            status == TriageReportStatus.Failed)
        {
            throw new TriageReportValidationException("publish_report status must be Completed or InsufficientEvidence.");
        }

        var classification = ReadReportString(root, "classification");
        ValidateClassification(status, classification);

        var confidence = ReadReportString(root, "confidence");
        if (!AllowedConfidence.Contains(confidence))
        {
            throw new TriageReportValidationException("publish_report confidence must be Low, Medium, or High.");
        }

        var documentationFit = ReadDocumentationFit(root);
        var evidence = ReadEvidence(root);
        if (status == TriageReportStatus.Completed && evidence.Count == 0)
        {
            throw new TriageReportValidationException("Completed reports must include at least one evidence item.");
        }

        return new TriageReport(
            status,
            ReadBoundedReportString(root, "summary", MaxSummaryLength, OrchestratorRepromptDiagnostics.SummaryTooLong),
            classification,
            confidence,
            evidence,
            ReadLimitations(root),
            ReadBoundedReportString(
                root,
                "recommendedNextAction",
                MaxRecommendedNextActionLength,
                OrchestratorRepromptDiagnostics.RecommendedNextActionTooLong))
        {
            DocumentationFit = documentationFit
        };
    }

    private static void ValidateClassification(TriageReportStatus status, string classification)
    {
        if (!TriageClassificationVocabulary.Contains(classification))
        {
            throw new TriageReportValidationException("publish_report classification is not supported.");
        }

        if (status == TriageReportStatus.InsufficientEvidence &&
            !string.Equals(classification, TriageClassificationVocabulary.Unknown, StringComparison.Ordinal))
        {
            throw new TriageReportValidationException("InsufficientEvidence reports must use classification Unknown.");
        }

        if (status == TriageReportStatus.Completed &&
            string.Equals(classification, TriageClassificationVocabulary.Unknown, StringComparison.Ordinal))
        {
            throw new TriageReportValidationException("Completed reports must use a concrete non-Unknown classification.");
        }
    }

    private static DocumentationFitStatus ReadDocumentationFit(JsonElement root)
    {
        var documentationFitName = ReadReportString(root, "documentationFit");
        if (!Enum.TryParse<DocumentationFitStatus>(documentationFitName, ignoreCase: false, out var documentationFit) ||
            !Enum.IsDefined(documentationFit))
        {
            throw new TriageReportValidationException(
                "publish_report documentationFit must be Current, CurrentWithHistorical, StaleOnly, Missing, or MultipleCurrentDocuments.");
        }

        return documentationFit;
    }

    /// <summary>
    /// Exactly one envelope is accepted per call: <c>report_json</c> alone, <c>report</c> alone, or a
    /// bare report carrying neither. A wrapper beside any other top-level property, the other wrapper
    /// or a repeat of itself included, is refused rather than resolved by precedence, so the backend
    /// never silently picks one of two reports.
    /// </summary>
    private static JsonElement ResolveReportRoot(JsonElement arguments)
    {
        JsonElementReader.RequireObject(
            arguments,
            "publish_report arguments must be an object.",
            CreateException);

        var hasReportJson = arguments.TryGetProperty(ReportJsonWrapper, out var reportJson);
        var hasReport = arguments.TryGetProperty(ReportWrapper, out var report);
        if (hasReportJson && hasReport)
        {
            throw new TriageReportValidationException(OrchestratorRepromptDiagnostics.ReportEnvelopeHasBothWrappers);
        }

        if (!hasReportJson && !hasReport)
        {
            return arguments;
        }

        if (arguments.EnumerateObject().Count() != 1)
        {
            throw new TriageReportValidationException(hasReportJson
                ? OrchestratorRepromptDiagnostics.ReportJsonWrapperNotAlone
                : OrchestratorRepromptDiagnostics.ReportWrapperNotAlone);
        }

        return hasReportJson
            ? JsonElementReader.RequireObject(reportJson, "publish_report report_json must be an object.", CreateException)
            : JsonElementReader.RequireObject(report, "publish_report report must be an object.", CreateException);
    }

    private static List<TriageReportEvidenceReference> ReadEvidence(JsonElement root)
    {
        if (!root.TryGetProperty("evidence", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            throw new TriageReportValidationException("publish_report evidence must be an array.");
        }

        if (element.GetArrayLength() > MaxEvidenceItems)
        {
            throw new TriageReportValidationException(OrchestratorRepromptDiagnostics.TooManyEvidenceItems);
        }

        var evidence = new List<TriageReportEvidenceReference>();
        foreach (var item in element.EnumerateArray())
        {
            JsonElementReader.RequireObject(
                item,
                "publish_report evidence items must be objects.",
                CreateException);

            var referenceId = ReadReportString(item, "referenceId");
            var quote = JsonElementReader.ReadOptionalString(
                item,
                "quote",
                CreateException,
                "publish_report quote must be a string.");
            if (quote is not null)
            {
                RequireWithinLength(quote, MaxQuoteLength, OrchestratorRepromptDiagnostics.QuoteTooLong);
            }

            evidence.Add(new TriageReportEvidenceReference(referenceId, quote));
        }

        return evidence;
    }

    private static string ReadReportString(JsonElement root, string propertyName)
    {
        return JsonElementReader.ReadRequiredString(
            root,
            propertyName,
            $"publish_report is missing string {propertyName}.",
            CreateException,
            $"publish_report {propertyName} must be a string.");
    }

    private static string ReadBoundedReportString(
        JsonElement root,
        string propertyName,
        int maxLength,
        string overLimitMessage)
    {
        var value = JsonElementReader.ReadRequiredString(
            root,
            propertyName,
            $"publish_report is missing string {propertyName}.",
            CreateException,
            $"publish_report {propertyName} must be a string.",
            trim: false);
        RequireWithinLength(value, maxLength, overLimitMessage);
        return value.Trim();
    }

    private static List<string> ReadLimitations(JsonElement root)
    {
        if (!root.TryGetProperty("limitations", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            throw new TriageReportValidationException("publish_report limitations must be an array of strings.");
        }

        if (element.GetArrayLength() > MaxLimitationItems)
        {
            throw new TriageReportValidationException(OrchestratorRepromptDiagnostics.TooManyLimitations);
        }

        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new TriageReportValidationException("publish_report limitations must contain only strings.");
            }

            var value = item.GetString()!;
            RequireWithinLength(value, MaxLimitationLength, OrchestratorRepromptDiagnostics.LimitationTooLong);
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value.Trim());
            }
        }

        return values;
    }

    /// <summary>
    /// A bound is measured on the value as sent, before trimming, and in Unicode code points, which is
    /// how JSON Schema <c>maxLength</c> counts, so a value the advertised schema admits is never refused
    /// here for a surrogate pair. An over-limit value is refused, never truncated.
    /// </summary>
    private static void RequireWithinLength(string value, int maxLength, string overLimitMessage)
    {
        if (value.Length > maxLength && value.EnumerateRunes().Count() > maxLength)
        {
            throw new TriageReportValidationException(overLimitMessage);
        }
    }

    private static Exception CreateException(string message)
    {
        return new TriageReportValidationException(message);
    }
}
