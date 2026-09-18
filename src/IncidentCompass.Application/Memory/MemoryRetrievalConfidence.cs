namespace IncidentCompass.Application.Memory;

/// <summary>
/// The band reported as <c>retrievalConfidence</c>. It says whether a returned document was confirmed
/// as describing the incident the trigger signal describes, not how high its vector score was and not
/// how well it answered the query the role wrote. The numeric <c>score</c> field is reported unchanged
/// beside it.
/// </summary>
/// <remarks>
/// <para>
/// Every band is decided against the fault query <see cref="MemoryFaultQuery" /> builds from the
/// signal. The role's query decides admission and order only: a band decided against it is one the
/// role could raise by re-querying with a document's own words.
/// </para>
/// <para>
/// A band never claims more than the weaker of the two judgements says. On a judged call <c>high</c>
/// needs the relevance judge to confirm the document against the fault query and every counted word of
/// the fault query to occur in it, <c>medium</c> is the judge's confirmation alone, and <c>low</c> is
/// admitted but unconfirmed. On a call no judge judged every item is <c>low</c>: word overlap alone
/// cannot confirm honestly, because a short fault query confirms on one shared word and a fault in
/// another script is left with only its Latin identifiers. A fault query with no counted word confirms
/// nothing either.
/// </para>
/// </remarks>
internal static class MemoryRetrievalConfidence
{
    /// <summary>
    /// The property name the band is written under, in the tool result, in the durable artifact
    /// payload and in the memory role's output. Named here because configuration load has to refuse
    /// a redaction attribute key that would replace it.
    /// </summary>
    public const string PayloadPropertyName = "retrievalConfidence";

    /// <summary>
    /// The property name the relevance judge's score for the fault query is written under, beside
    /// <c>judgeScore</c>, in the tool result and in the durable artifact payload. It is what the band
    /// was decided from, so configuration load refuses a redaction attribute key that would replace
    /// it, for the same reason it refuses one naming the band.
    /// </summary>
    public const string ConfirmationScorePropertyName = "confirmationScore";

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
    /// The band of a judged call. An item the judge did not confirm against the fault query is
    /// <c>low</c> whatever its lexical coverage, and full lexical coverage of the fault query lifts a
    /// confirmed item to <c>high</c> only because that is the two judgements agreeing. A fault query
    /// with no counted word confirms nothing, because the lexical gate's rule that such a query
    /// constrains nothing is an admission rule, not a confirmation.
    /// </summary>
    public static string JudgedBand(bool confirmed, MemorySearchLexicalSupport faultSupport)
    {
        if (!confirmed || faultSupport.CountedQueryWords == 0)
        {
            return Low;
        }

        return faultSupport.IsFullyCovered ? High : Medium;
    }
}
