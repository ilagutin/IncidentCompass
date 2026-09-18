namespace IncidentCompass.Application.Memory;

/// <summary>
/// Bands the matches <c>memory_search</c> is about to return, against the fault query rather than
/// against the role's query. This is the only step that raises a band above <c>low</c>, and only a
/// relevance judge can make it do so.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a second judgement.</b> Admission and order answer the role's query, and have to: that
/// query is what the role is looking for. Confirmation answers a different question, whether the
/// document describes the incident the signal describes, and the role must not be able to answer it by
/// choosing its words. A role that re-queried with a document's own quote used to get that document
/// confirmed, which would let a <c>KnownIncident</c> classification rest on a document the fault never
/// described.
/// </para>
/// <para>
/// <b>With a judge.</b> When the relevance judge judged the call, the returned matches, and only
/// those, are scored a second time against the fault query. At or above the confirm score an item is
/// <c>medium</c>, or <c>high</c> when every counted word of the fault query also occurs in it;
/// otherwise it is <c>low</c>. The floor is not applied to this score: admission has already
/// happened, and a document that does not describe the fault is still related context. The second
/// call goes through <see cref="MemoryRelevanceJudgePass.ScoreAsync" />, so it fails exactly as the
/// first does: every failure propagates with its own code, and a non-finite score or a wrong score
/// count is refused by name.
/// </para>
/// <para>
/// <b>Without one, nothing is confirmed.</b> On a call no judge judged, because the host runs none or
/// the judge is turned off, every item is <c>low</c>. The same holds when the judge answered the
/// admission call and then reports itself absent on the confirmation call: every item is <c>low</c>
/// and the result says why. There is no lexical fallback. Word overlap between a short fault query
/// and a document confirms on a single shared word, and a fault written in another script leaves only
/// its Latin identifiers eligible, so a document would be confirmed on the service name alone. No
/// lexical threshold can be chosen honestly against those two, so confirmation needs the judge.
/// Retrieval, admission, the vector-only fallback and passing items on as context all still work.
/// </para>
/// <para>
/// <b>Without a fault.</b> No signal, or a fault query with no counted word, confirms nothing and every
/// band is <c>low</c>. The judge is not asked, because there is nothing to confirm against.
/// </para>
/// </remarks>
internal static class MemoryFaultConfirmation
{
    /// <summary>
    /// Stated in the tool output when the judge answered the admission call and then reported itself
    /// absent on the confirmation call. It is not an error code: nothing failed, and nothing was
    /// confirmed.
    /// </summary>
    public const string JudgeAbsentAtConfirmationLimitation =
        "no relevance judge ran to confirm these matches, so none was confirmed";

    public static async Task<MemoryFaultConfirmationResult> ConfirmAsync(
        IMemoryRelevanceJudge? judge,
        string? faultQuery,
        IReadOnlyList<MemorySearchRankedMatch> ranked,
        MemoryRelevanceJudgement judgement,
        MemoryRelevanceJudgeSettings settings,
        CancellationToken cancellationToken)
    {
        if (ranked.Count == 0 ||
            !judgement.Judged ||
            judge is null ||
            faultQuery is null ||
            !MemorySearchLexicalFilter.HasCountedWord(faultQuery))
        {
            return new MemoryFaultConfirmationResult(Unconfirmed(ranked), Limitation: null);
        }

        var scores = await MemoryRelevanceJudgePass.ScoreAsync(
            judge,
            faultQuery,
            ranked.Select(static match => match.Match.Text).ToArray(),
            cancellationToken);
        if (scores is null)
        {
            return new MemoryFaultConfirmationResult(Unconfirmed(ranked), JudgeAbsentAtConfirmationLimitation);
        }

        return new MemoryFaultConfirmationResult(
            ranked
                .Select((match, index) => match with
                {
                    RetrievalConfidence = MemoryRetrievalConfidence.JudgedBand(
                        scores[index] >= settings.ConfirmScore,
                        MemorySearchLexicalFilter.Evaluate(faultQuery, match.Match.Text)),
                    ConfirmationScore = scores[index]
                })
                .ToArray(),
            Limitation: null);
    }

    private static MemorySearchRankedMatch[] Unconfirmed(IReadOnlyList<MemorySearchRankedMatch> ranked) =>
        ranked
            .Select(static match => match with
            {
                RetrievalConfidence = MemoryRetrievalConfidence.Low,
                ConfirmationScore = null
            })
            .ToArray();
}
