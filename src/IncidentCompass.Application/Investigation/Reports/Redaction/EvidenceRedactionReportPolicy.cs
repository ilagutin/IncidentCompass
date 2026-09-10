namespace IncidentCompass.Application.Investigation.Reports.Redaction;

/// <summary>
/// Adds the backend's own statement that evidence behind a report was withheld from the model, so a
/// human reading the conclusion knows part of the input was not part of it. It follows
/// <see cref="Context.ContextOutcomeReportPolicy" />: a report limitation the backend derives at
/// publication rather than a claim the model is asked to make about itself.
/// <para>
/// Why the model is not asked: a model-authored "nothing was redacted" is worth nothing, because the
/// model cannot see what it was not shown, and a model-authored "something was redacted" is worth
/// less than nothing, because prompt text reaching the model can ask it to say so.
/// </para>
/// <para>
/// Why the payload is not read either: after redaction, a value the redactor replaced and connector
/// text that already contained the literal <c>[REDACTED]</c> are the same bytes. A marker derived by
/// searching stored payload text for that literal would therefore be raised by whoever wrote the
/// ticket or the source file. The marker instead comes from
/// <c>triage_artifacts.redaction_applied</c>, which the redaction boundary writes from a comparison
/// against the pre-redaction document, at the one moment the two are distinguishable.
/// </para>
/// <para>
/// "Relevant" is therefore defined as: at least one artifact this report cites carries a recorded
/// redaction outcome of true. Nothing weaker is observable without guessing, and nothing stronger is
/// true - an artifact whose outcome was never recorded contributes nothing in either direction,
/// which is why the marker's absence is documented as "no cited artifact is known to have been
/// redacted" rather than as "nothing was redacted".
/// </para>
/// </summary>
internal static class EvidenceRedactionReportPolicy
{
    /// <summary>
    /// The reserved sentence. This policy owns exactly this string in both directions: it is
    /// appended when the backend derived the marker and dropped when it did not, so a reader who
    /// recognizes this wording is reading the backend, never the model.
    /// <para>
    /// The claim stops at the string. Ownership is an ordinal equality check, so a model-authored
    /// paraphrase - a different case, an extra clause, a leading "Note:" - is not this sentence and
    /// is left standing as the model's own limitation, exactly like every other line the model
    /// wrote. That is the deliberate choice, not an oversight in the matcher. The direction that
    /// matters is covered without any matching at all: when the backend does derive the marker it
    /// appends the sentence, so no model output can suppress a real withholding. What a looser match
    /// would buy is only the removal of near-copies claiming a withholding that did not happen, and
    /// it would be bought by deleting report text on a fuzzy comparison, which is the one direction
    /// that can destroy a genuine limitation a human needed to read. Normalization also has no
    /// principled stopping point - case, whitespace, punctuation, then wording - and no stopping
    /// point short of a judgement call closes the gap, because a paraphrase with a real extra clause
    /// survives all of them. So the sentence is reserved rather than the meaning, and a paraphrase
    /// carries no more authority than any other sentence the model wrote.
    /// </para>
    /// </summary>
    public const string WithheldEvidenceLimitation =
        "Evidence cited by this report includes at least one value that redaction removed before the model saw it.";

    public static TriageReport Apply(
        TriageReport report,
        IReadOnlyCollection<CitedEvidenceRedaction> citedEvidence)
    {
        // Ordinal equality, deliberately: this policy deletes report text, and the only text it is
        // entitled to delete is its own sentence, byte for byte. See the remark on the constant.
        var limitations = report.Limitations
            .Where(limitation => !string.Equals(limitation, WithheldEvidenceLimitation, StringComparison.Ordinal))
            .ToList();
        if (citedEvidence.Any(evidence => evidence.RedactionApplied == true))
        {
            limitations.Add(WithheldEvidenceLimitation);
        }

        return report with { Limitations = limitations };
    }
}
