using IncidentCompass.Application.Core.Text;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Memory;

/// <summary>
/// The query the backend builds from the trigger signal, and against which every returned
/// <c>memory_search</c> item is confirmed. The role's own query decides what is admitted and in which
/// order; this one decides whether an admitted document was confirmed as describing the incident.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the band is not decided against the role's query.</b> The role writes its query, so a band
/// decided against it is a band the role can raise: a role that re-queries with a document's own words
/// gets that document confirmed, whatever the fault was. The fields this query is built from are
/// supplied by whoever sent the signal, not written by the backend, but no model output reaches them,
/// so a role cannot raise the band of a document it has already been shown by choosing its words.
/// </para>
/// <para>
/// <b>What it is built from.</b> The signal's service name, error type and error message, in that order,
/// joined with one space, blank parts skipped. That is exactly what the memory role instructions tell
/// the role to search with, and the mock memory role composes its query through <see cref="Compose" />
/// for that reason, so on the demo path the two agree. When the error message is blank, which is every
/// user and manual report, the summary takes its place, because it is then the only description the
/// signal has, but only when the sender supplied that summary. On a user or manual report the summary
/// is the reporter's own text, so a confirmation resting on it is only as trustworthy as the reporter.
/// </para>
/// <para>
/// <b>The backend's own summary never takes that place.</b> Intake also synthesizes a summary for a
/// structured signal that arrives without one, including one with an error type and no message, and
/// that synthesis is the templated frame described below. It is recognized by recomputing it with the
/// production <see cref="SummarySynthesizer" /> from the signal's own fields and comparing, not by
/// guessing from the source kind, so a sender-supplied summary is still used and the frame never is.
/// Every signal in the retrieval benchmark carries a message, so this rule moves no measured number.
/// </para>
/// <para>
/// <b>Why the synthesized summary is left out otherwise.</b> Intake synthesizes a summary of the form
/// <c>&lt;service&gt;: &lt;operation or route&gt; failed - &lt;type&gt;: &lt;message&gt;</c>, and its
/// templated "failed" frame turns any signal into a failure of that service, which pulls it towards
/// every incident document about the service. Measured through the product on the retrieval benchmark,
/// the off-topic checkout price-rounding query scored 2.97 against the known checkout-timeout incident
/// with the summary in its fault query, above the confirm score, and -1.15 against the role's own query.
/// </para>
/// <para>
/// <b>The bound.</b> The result is cut on a rune boundary to
/// <see cref="MemorySearchQueryBound.MaxQueryCharacters" />, the same bound the role's query is held
/// to, because the relevance judge reads this query inside the same token window.
/// </para>
/// </remarks>
internal static class MemoryFaultQuery
{
    /// <summary>
    /// The fault query of <paramref name="signal" />, or null when there is no signal. Production never
    /// passes null, because the trigger signal is required by the schema and by the context loader; a
    /// null here comes only from a hand-built context, and the caller bands such a call unconfirmed.
    /// A re-triage loads the fault's original trigger signal, so it confirms against that signal too.
    /// </summary>
    public static string? For(Signal? signal) =>
        signal is null
            ? null
            : Compose(
                signal.ServiceName,
                signal.ErrorType,
                signal.ErrorMessage,
                IsSynthesizedSummary(signal) ? null : signal.Summary);

    /// <summary>
    /// Whether the signal's summary is the one intake would synthesize from its own fields, which is
    /// the backend's frame rather than anything the sender said.
    /// </summary>
    private static bool IsSynthesizedSummary(Signal signal) =>
        string.Equals(
            signal.Summary,
            SummarySynthesizer.ForStructuredSignal(
                signal.ServiceName,
                signal.OperationName,
                signal.HttpRoute,
                signal.ErrorType,
                signal.ErrorMessage),
            StringComparison.Ordinal);

    /// <summary>
    /// The service name, the error type and the error message, in this order, joined with one space,
    /// blank parts skipped, bounded. <paramref name="summary" /> stands in for a blank message.
    /// </summary>
    public static string Compose(string? serviceName, string? errorType, string? errorMessage, string? summary)
    {
        var description = string.IsNullOrWhiteSpace(errorMessage) ? summary : errorMessage;
        var joined = string.Join(
            ' ',
            new[] { serviceName, errorType, description }
                .Where(static part => !string.IsNullOrWhiteSpace(part)));
        return TextTruncator.Truncate(joined, MemorySearchQueryBound.MaxQueryCharacters);
    }
}
