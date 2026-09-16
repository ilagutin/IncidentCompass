using System.Globalization;
using System.Text;

namespace IncidentCompass.Application.Investigation.Reports;

/// <summary>
/// Text only the backend may write into a report or its publication entry, and the comparison that
/// decides whether a model wrote it anyway.
/// </summary>
/// <remarks>
/// <para>
/// The durable authorship marker is <see cref="BackendAuthoredLedgerPrefix"/> on the
/// <c>ReportPublished</c> ledger rationale, written only when <see cref="TriageReport.BackendAuthored"/>
/// is set, which only the backend termination path does. The reserved sentences are defence in depth
/// for a reader of the report itself.
/// </para>
/// <para>
/// The comparison is deliberately loose so a near-copy cannot slip past an ordinal match: text is
/// NFKC-normalized, zero-width and format characters are removed, every run of whitespace (the
/// non-breaking kinds included) becomes one space, the ends are trimmed, and the result is compared
/// ignoring case.
/// </para>
/// </remarks>
internal static class ReservedReportText
{
    /// <summary>Opens the <c>ReportPublished</c> rationale of a backend-authored report, and no other.</summary>
    public const string BackendAuthoredLedgerPrefix = "backend_authored: ";

    /// <summary>The fixed refusal a model-authored report using reserved text is reprompted with.</summary>
    public const string ReservedTextRefusal =
        "publish_report must not use the summary, limitation or ledger marker the backend reserves for its own reports.";

    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var composed = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(composed.Length);
        var pendingSpace = false;
        foreach (var character in composed)
        {
            if (char.IsWhiteSpace(character) || character == ' ')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    public static bool EqualsReserved(string value, string reserved) =>
        string.Equals(Normalize(value), Normalize(reserved), StringComparison.OrdinalIgnoreCase);

    public static bool ContainsReserved(string value, string reserved) =>
        Normalize(value).Contains(Normalize(reserved), StringComparison.OrdinalIgnoreCase);

    public static bool StartsWithBackendMarker(string value) =>
        Normalize(value).StartsWith(Normalize(BackendAuthoredLedgerPrefix), StringComparison.OrdinalIgnoreCase);
}
