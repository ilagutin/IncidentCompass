using IncidentCompass.Application.Core.Errors;

namespace IncidentCompass.Application.Memory;

/// <summary>
/// Asks <see cref="IMemoryRelevanceJudge" /> about one candidate set and turns its scores into an
/// admission decision. This is the only place the two configured thresholds are applied.
/// </summary>
/// <remarks>
/// <para>
/// The judge is asked about every query, not only about a query the lexical gate left nothing for.
/// The gate's measured failure is not that it returns nothing: on a query whose eligible words are
/// all Latin identifiers it returns a full set and reports it as confirmed, which a judge that only
/// ran on an empty gate result would never see.
/// </para>
/// <para>
/// Exactly two states are a deployment shape rather than a failure, and they are named by
/// <see cref="MemoryRelevanceJudgeAbsence" />: no judge model directory is configured, and nothing is
/// installed in the configured one yet. Those return unjudged with a limitation the tool reports.
/// Everything else propagates untouched, a judge that does not verify included, because a judge that
/// was installed and then could not load, could not run or no longer matches its digest has said
/// nothing about relevance, and answering anyway would be the tool inventing the answer it was built
/// to stop inventing.
/// </para>
/// <para>
/// The adapter's side of the port contract is checked here too. A score that is not a finite number,
/// or a count that does not match the candidates, is refused by name rather than used: a NaN compares
/// false against both thresholds, so an unchecked one would be admitted as an unconfirmed match.
/// </para>
/// </remarks>
internal static class MemoryRelevanceJudgePass
{
    /// <summary>
    /// The name a contract refusal carries. The port, not any one adapter, refused these, so naming
    /// an adapter would say the failure came from somewhere it did not.
    /// </summary>
    private const string PortName = "memory_relevance_judge";

    /// <summary>
    /// Stated in the tool output so the reading role knows the result was admitted without a judge.
    /// It is not an error code: nothing failed, this host simply runs no judge.
    /// </summary>
    public const string NoJudgeOnHostLimitation =
        "no relevance judge is installed on this host, so matches were admitted by lexical support alone";

    public static async Task<MemoryRelevanceJudgement> JudgeAsync(
        IMemoryRelevanceJudge? judge,
        string query,
        IReadOnlyList<MemorySearchMatch> candidates,
        MemoryRelevanceJudgeSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Mode == MemoryRelevanceJudgeMode.Off)
        {
            return MemoryRelevanceJudgement.NotJudged(limitation: null);
        }

        if (judge is null)
        {
            return MemoryRelevanceJudgement.NotJudged(NoJudgeOnHostLimitation);
        }

        // An empty candidate set is judged, and trivially admits nothing. Saying so rather than
        // reporting it unjudged keeps the empty answer on the same branch as every other judged
        // answer, and the port does no work for an empty list either.
        if (candidates.Count == 0)
        {
            return MemoryRelevanceJudgement.Admitting([]);
        }

        IReadOnlyList<float> scores;
        try
        {
            scores = await judge.ScoreAsync(
                query,
                candidates.Select(static candidate => candidate.Text).ToArray(),
                cancellationToken);
        }
        catch (MemoryRelevanceJudgeException exception) when (IsNoJudgeOnHost(exception))
        {
            return MemoryRelevanceJudgement.NotJudged(NoJudgeOnHostLimitation);
        }

        if (scores.Count != candidates.Count)
        {
            throw ContractRefusal(
                "The relevance judge returned " + scores.Count + " scores for " + candidates.Count +
                " candidates; the port requires one score per candidate in order.",
                MemoryRelevanceJudgeErrorCodes.ScoreCountMismatch);
        }

        return MemoryRelevanceJudgement.Admitting(Admit(candidates, scores, settings));
    }

    /// <summary>
    /// Whether this host is not running a judge at all, which is the only state the caller may answer
    /// without one. It is decided on the adapter's own code, not on the normalized code: that
    /// normalizes a digest mismatch, a missing model file, a failed fetch, an oversized download, an
    /// install timeout, an invalid manifest and an unreadable model store to the same
    /// <c>memory_relevance_judge_unavailable</c>, and every one of those is a broken host rather than
    /// a judge-less one.
    /// </summary>
    private static bool IsNoJudgeOnHost(MemoryRelevanceJudgeException exception) =>
        string.Equals(
            exception.ErrorCode,
            MemoryRelevanceJudgeErrorCodes.Unavailable,
            StringComparison.Ordinal) &&
        MemoryRelevanceJudgeAbsence.IsAbsent(exception.ProviderErrorCode);

    private static List<MemoryRelevanceJudgedCandidate> Admit(
        IReadOnlyList<MemorySearchMatch> candidates,
        IReadOnlyList<float> scores,
        MemoryRelevanceJudgeSettings settings)
    {
        var admitted = new List<MemoryRelevanceJudgedCandidate>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            double score = scores[index];
            if (!double.IsFinite(score))
            {
                throw ContractRefusal(
                    "The relevance judge scored candidate " + index + " of " + candidates.Count +
                    " as a value that is not a finite number, so it cannot be compared with either" +
                    " threshold.",
                    MemoryRelevanceJudgeErrorCodes.ScoreNotFinite);
            }

            if (score < settings.FloorScore)
            {
                continue;
            }

            admitted.Add(new MemoryRelevanceJudgedCandidate(
                candidates[index],
                score,
                score >= settings.ConfirmScore));
        }

        return admitted;
    }

    /// <summary>
    /// A refusal of the port's own contract. It carries no query, candidate or score, only the
    /// position and the count, and its failure kind is the one a malformed provider answer has.
    /// </summary>
    private static MemoryRelevanceJudgeException ContractRefusal(string message, string errorCode) =>
        new(PortName, message, errorCode: errorCode, failureKind: ProviderFailureKind.InvalidResponse);
}
