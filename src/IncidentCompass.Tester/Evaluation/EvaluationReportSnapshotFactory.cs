using System.Text;
using System.Text.RegularExpressions;

namespace IncidentCompass.Tester.Evaluation;

internal static class EvaluationReportSnapshotFactory
{
    internal const int MaximumEvidenceItems = 20;
    internal const int MaximumSummaryLength = 2000;
    internal const int MaximumRecommendedActionLength = 1000;
    internal const int MaximumMetadataLength = 256;
    internal const int MaximumLimitations = 20;
    internal const int MaximumLimitationLength = 500;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex AuthorizationPattern = new(
        @"\b(Bearer|Basic)\s+[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);
    private static readonly Regex CredentialAssignmentPattern = new(
        @"[""']?\b(access[-_ ]?token|api[-_ ]?key|authorization|client[-_ ]?secret|connection[-_ ]?string|password|passwd|private[-_ ]?key|refresh[-_ ]?token|secret|token|x[-_ ]?api[-_ ]?key)\b[""']?\s*[:=]\s*(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[^\s,;}]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);
    private static readonly Regex TruncatedDoubleQuotedCredentialPattern = new(
        @"[""']?\b(access[-_ ]?token|api[-_ ]?key|authorization|client[-_ ]?secret|connection[-_ ]?string|password|passwd|private[-_ ]?key|refresh[-_ ]?token|secret|token|x[-_ ]?api[-_ ]?key)\b[""']?\s*[:=]\s*""(?:\\.|[^""\\])*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);
    private static readonly Regex TruncatedSingleQuotedCredentialPattern = new(
        @"[""']?\b(access[-_ ]?token|api[-_ ]?key|authorization|client[-_ ]?secret|connection[-_ ]?string|password|passwd|private[-_ ]?key|refresh[-_ ]?token|secret|token|x[-_ ]?api[-_ ]?key)\b[""']?\s*[:=]\s*'(?:\\.|[^'\\])*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);
    private static readonly Regex KnownCredentialPrefixPattern = new(
        @"\b(?:sk|gh[pousr])[-_][A-Za-z0-9_-]{8,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        MatchTimeout);
    private static readonly Regex UriUserInfoPattern = new(
        @"(?<=://)[^\s/@:]+:[^\s/@]+@",
        RegexOptions.CultureInvariant,
        MatchTimeout);

    public static EvaluationReportSnapshot? Create(TriageReportResponse? report)
    {
        if (report is null)
        {
            return null;
        }

        var evidence = report.Evidence.Take(MaximumEvidenceItems).Select(item => new EvaluationEvidenceSnapshot(
            item.Id,
            Sanitize(item.Kind, MaximumMetadataLength),
            item.ArtifactId,
            Sanitize(item.ArtifactKind, MaximumMetadataLength),
            SanitizeNullable(item.ArtifactDomainRef, MaximumMetadataLength),
            Sanitize(item.Reference, MaximumMetadataLength),
            item.Score)).ToArray();
        var limitations = report.Limitations.Take(MaximumLimitations)
            .Select(static item => Sanitize(item, MaximumLimitationLength))
            .ToArray();
        return new EvaluationReportSnapshot(
            report.Id,
            Sanitize(report.Status, MaximumMetadataLength),
            Sanitize(report.Summary, MaximumSummaryLength),
            Sanitize(report.RecommendedNextAction, MaximumRecommendedActionLength),
            Sanitize(report.Classification, MaximumMetadataLength),
            Sanitize(report.DocumentationFit, MaximumMetadataLength),
            Sanitize(report.ConfigHash, MaximumMetadataLength),
            limitations,
            Math.Max(0, report.Limitations.Count - limitations.Length),
            evidence,
            Math.Max(0, report.Evidence.Count - evidence.Length));
    }

    private static string? SanitizeNullable(string? value, int maximumLength) =>
        value is null ? null : Sanitize(value, maximumLength);

    private static string Sanitize(string value, int maximumLength)
    {
        var inspectionLimit = checked(maximumLength + 512);
        var normalized = new StringBuilder(Math.Min(value.Length, inspectionLimit));
        foreach (var character in value)
        {
            normalized.Append(char.IsControl(character) ? ' ' : character);
            if (normalized.Length == inspectionLimit)
            {
                break;
            }
        }

        try
        {
            var candidate = normalized.ToString();
            if (value.Length > inspectionLimit &&
                (TruncatedDoubleQuotedCredentialPattern.IsMatch(candidate) ||
                 TruncatedSingleQuotedCredentialPattern.IsMatch(candidate)))
            {
                return "[REDACTED:TRUNCATED_CREDENTIAL]";
            }

            var sanitized = AuthorizationPattern.Replace(candidate, "$1 [REDACTED]");
            sanitized = CredentialAssignmentPattern.Replace(sanitized, "$1=[REDACTED]");
            sanitized = KnownCredentialPrefixPattern.Replace(sanitized, "[REDACTED]");
            sanitized = UriUserInfoPattern.Replace(sanitized, "[REDACTED]@");
            return sanitized.Length <= maximumLength ? sanitized : sanitized[..maximumLength];
        }
        catch (RegexMatchTimeoutException)
        {
            return "[REDACTED:SANITIZATION_TIMEOUT]";
        }
    }
}
