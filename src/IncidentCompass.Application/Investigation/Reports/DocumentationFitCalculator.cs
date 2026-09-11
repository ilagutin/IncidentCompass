namespace IncidentCompass.Application.Investigation.Reports;

/// <summary>
/// The single definition of how the statuses of the retrieved documents a report cites aggregate
/// into one <see cref="DocumentationFitStatus"/>.
/// <para>
/// The backend derives the report's value with this and refuses a <c>publish_report</c> whose value
/// differs, so the orchestrator is being asked for the output of a function rather than for a
/// judgement. The shipped orchestrator instructions state the same rule in words, and
/// <c>ShippedOrchestratorInstructionExampleTests</c> evaluates the shipped one-shot example through
/// this method, so the instruction and the validator cannot drift apart silently.
/// </para>
/// <para>
/// Only <c>Current</c> and <c>Stale</c> documents count. <c>Unversioned</c> and
/// <c>ServiceMismatch</c> are the backend saying it could not assess the document at all, so they
/// raise no fit on their own and a report that cites only those is <c>Missing</c>, exactly as a
/// report that cites no document is.
/// </para>
/// </summary>
internal static class DocumentationFitCalculator
{
    internal const string CurrentDocument = "Current";
    internal const string StaleDocument = "Stale";

    public static DocumentationFitStatus Resolve(IEnumerable<string?> citedDocumentStatuses)
    {
        ArgumentNullException.ThrowIfNull(citedDocumentStatuses);

        var currentCount = 0;
        var staleCount = 0;
        foreach (var status in citedDocumentStatuses)
        {
            if (string.Equals(status, CurrentDocument, StringComparison.Ordinal))
            {
                currentCount++;
            }
            else if (string.Equals(status, StaleDocument, StringComparison.Ordinal))
            {
                staleCount++;
            }
        }

        return currentCount switch
        {
            > 1 => DocumentationFitStatus.MultipleCurrentDocuments,
            1 when staleCount > 0 => DocumentationFitStatus.CurrentWithHistorical,
            1 => DocumentationFitStatus.Current,
            _ when staleCount > 0 => DocumentationFitStatus.StaleOnly,
            _ => DocumentationFitStatus.Missing
        };
    }
}
