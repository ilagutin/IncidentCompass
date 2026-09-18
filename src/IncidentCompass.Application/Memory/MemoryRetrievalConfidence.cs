namespace IncidentCompass.Application.Memory;

/// <summary>
/// The band reported as <c>retrievalConfidence</c>. It describes how a match was admitted, not how
/// high its vector score was: on the shipped multilingual embedding model relevant and unrelated
/// chunks score alike, so a similarity threshold carried no information. The numeric <c>score</c>
/// field is reported unchanged beside it.
/// </summary>
/// <remarks>
/// A band never claims more than the weaker of the two judgements says. On a judged call <c>high</c>
/// needs the relevance judge to confirm and the lexical gate to be fully covered, so it means both
/// agree strongly; <c>medium</c> is the judge alone; <c>low</c> is admitted but unconfirmed. On an
/// unjudged call the lexical gate is the only judgement there is, and the three bands keep the
/// meanings they had before a judge existed.
/// </remarks>
internal static class MemoryRetrievalConfidence
{
    /// <summary>
    /// The property name the band is written under, in the tool result, in the durable artifact
    /// payload and in the memory role's output. Named here because configuration load has to refuse
    /// a redaction attribute key that would replace it.
    /// </summary>
    public const string PayloadPropertyName = "retrievalConfidence";

    /// <summary>Confirmed by everything that judged the match.</summary>
    public const string High = "high";

    /// <summary>Confirmed by one judgement while the other is partial.</summary>
    public const string Medium = "medium";

    /// <summary>Admitted without being confirmed by anything: related, not a stated match.</summary>
    public const string Low = "low";

    /// <summary>
    /// Whether a band confirms that the item describes this fault rather than merely relating to the
    /// query. <c>high</c> and <c>medium</c> confirm; <c>low</c> does not, and neither does an absent
    /// band.
    /// </summary>
    /// <remarks>
    /// Absence is not a weaker yes. A memory artifact written before this release carries no
    /// <c>retrievalConfidence</c> key at all, so nothing ever recorded a confirmation for it, and
    /// reading that silence as confirmation is the case this predicate exists to stop: a re-triage of
    /// an old fault would rest a <c>KnownIncident</c> classification, and the ticket and remediation
    /// diff that follow from it, on a match no judge ever saw. Reading it the safe way costs one
    /// correction turn on a report the backend can still publish under another classification.
    /// </remarks>
    public static bool ConfirmsMatch(string? band) => band is High or Medium;

    /// <summary>
    /// The band of an unjudged call: lexically supported and fully covered is <c>high</c>, lexically
    /// supported otherwise is <c>medium</c>, and the vector-only fallback is <c>low</c>.
    /// </summary>
    public static string Band(MemorySearchLexicalSupport support, bool vectorOnly)
    {
        if (vectorOnly)
        {
            return Low;
        }

        return support.IsFullyCovered ? High : Medium;
    }

    /// <summary>
    /// The band of a judged call. The judge decided admission, so an unconfirmed match is <c>low</c>
    /// whatever its lexical coverage, and full lexical coverage lifts a confirmed match to
    /// <c>high</c> only because that is the two judgements agreeing.
    /// </summary>
    public static string JudgedBand(bool confirmed, MemorySearchLexicalSupport support)
    {
        if (!confirmed)
        {
            return Low;
        }

        return support.IsFullyCovered ? High : Medium;
    }
}
