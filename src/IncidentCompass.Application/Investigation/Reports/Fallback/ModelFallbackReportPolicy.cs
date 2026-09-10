namespace IncidentCompass.Application.Investigation.Reports.Fallback;

/// <summary>
/// Adds the backend's own statement that part of this investigation ran on a fallback route, so a
/// human reading the conclusion knows the configured provider did not produce all of it. It follows
/// <see cref="Redaction.EvidenceRedactionReportPolicy" />: a report limitation the backend derives
/// at publication, from what the ledger recorded, rather than a claim the model is asked to make
/// about itself.
/// <para>
/// Why the model is not asked: a model answering on the fallback route has no way to know it is the
/// fallback. Nothing in its prompt says which route dispatched it or that an earlier call failed,
/// and adding that would be telling the model about the run's health so it can repeat it back.
/// </para>
/// <para>
/// Why the model provenance already on the report is not enough on its own. Provenance lists the
/// distinct routes, providers and models that answered, and a fallback does appear there. But an
/// attempt legitimately spans several routes - the orchestrator's, each delegated role's - so a
/// reader looking at a provenance list cannot tell a fallback entry from an ordinary second route.
/// The list says what answered; it does not say that something failed first. This sentence is the
/// part a reader cannot reconstruct, and provenance remains where the detail lives, which is why the
/// sentence names no route.
/// </para>
/// </summary>
internal static class ModelFallbackReportPolicy
{
    /// <summary>
    /// The reserved sentence. As with the redaction marker, this policy owns exactly this string in
    /// both directions: it is appended when the backend derived a fail-over and dropped when it did
    /// not, so a reader who recognizes this wording is reading the backend and never the model. The
    /// claim stops at the string, matched ordinally, so a model-authored paraphrase is left standing
    /// as the model's own limitation rather than deleted on a fuzzy comparison.
    /// </summary>
    public const string DegradedRoutingLimitation =
        "At least one model call behind this report failed on its configured route and was answered by that route's fallback.";

    public static TriageReport Apply(
        TriageReport report,
        IReadOnlyCollection<AttemptModelFallback> fallbacks)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(fallbacks);

        var limitations = report.Limitations
            .Where(limitation => !string.Equals(limitation, DegradedRoutingLimitation, StringComparison.Ordinal))
            .ToList();
        if (fallbacks.Count > 0)
        {
            limitations.Add(DegradedRoutingLimitation);
        }

        return report with { Limitations = limitations };
    }
}
