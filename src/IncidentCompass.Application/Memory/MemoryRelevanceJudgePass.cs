using IncidentCompass.Application.Core.Errors;

namespace IncidentCompass.Application.Memory;

/// <summary>
/// Asks <see cref="IMemoryRelevanceJudge" /> about one candidate set and turns its scores into an
/// admission decision. This is the only place the floor is applied, and <see cref="ScoreAsync" /> is
/// the only way either judgement of a call reaches the port.
/// </summary>
/// <remarks>
/// <para>
/// The judge is asked about every query, not only about a query the lexical gate left nothing for.
/// The gate's measured failure is not that it returns nothing: on a query whose eligible words are
/// all Latin identifiers it admits a full set of unrelated chunks, which before confirmation moved to
/// the judge were also reported as confirmed, and which a judge that only ran on an empty gate result
/// would never see. The gate itself confirms nothing: every match it admits is banded <c>low</c>.
/// </para>
/// <para>
/// Exactly two states are a deployment shape rather than a failure, and they are named by
/// <see cref="MemoryRelevanceJudgeAbsence" />: no judge model directory is configured, and nothing is
/// installed in the configured one yet. Those return unjudged with a limitation the tool reports, and
/// so does a judge turned off by configuration: on every unjudged call nothing is confirmed.
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
/// <para>
/// The confirm score is not applied here. Admission is decided against the role's query, and
/// confirmation against the fault query, by <see cref="MemoryFaultConfirmation" />, which asks the
/// port a second time through <see cref="ScoreAsync" /> and so under exactly these rules.
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
    /// Stated in the tool output so the reading role knows the result was admitted without a judge and
    /// that nothing in it is confirmed. It is not an error code: nothing failed, this call simply ran
    /// no judge, because the host has none or the judge is turned off.
    /// </summary>
    public const string NoJudgeLimitation =
        "no relevance judge ran, so matches were admitted by lexical support alone and none was confirmed";

    public static async Task<MemoryRelevanceJudgement> JudgeAsync(
        IMemoryRelevanceJudge? judge,
        string query,
        IReadOnlyList<MemorySearchMatch> candidates,
        MemoryRelevanceJudgeSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.Mode == MemoryRelevanceJudgeMode.Off)
        {
            return MemoryRelevanceJudgement.NotJudged(NoJudgeLimitation);
        }

        if (judge is null)
        {
            return MemoryRelevanceJudgement.NotJudged(NoJudgeLimitation);
        }

        // An empty candidate set is judged, and trivially admits nothing. Saying so rather than
        // reporting it unjudged keeps the empty answer on the same branch as every other judged
        // answer, and the port does no work for an empty list either.
        if (candidates.Count == 0)
        {
            return MemoryRelevanceJudgement.Admitting([]);
        }

        var scores = await ScoreAsync(
            judge,
            query,
            candidates.Select(static candidate => candidate.Text).ToArray(),
            cancellationToken);
        return scores is null
            ? MemoryRelevanceJudgement.NotJudged(NoJudgeLimitation)
            : MemoryRelevanceJudgement.Admitting(Admit(candidates, scores, settings));
    }

    /// <summary>
    /// Scores <paramref name="candidates" /> against <paramref name="query" /> under the port contract:
    /// one finite score per candidate, in order. Returns null only when this host is not running a
    /// judge at all; every other failure propagates with its own code, and a contract breach is
    /// refused by name.
    /// </summary>
    public static async Task<IReadOnlyList<double>?> ScoreAsync(
        IMemoryRelevanceJudge judge,
        string query,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<float> scores;
        try
        {
            scores = await judge.ScoreAsync(query, candidates, cancellationToken);
        }
        catch (MemoryRelevanceJudgeException exception) when (IsNoJudgeOnHost(exception))
        {
            return null;
        }

        if (scores.Count != candidates.Count)
        {
            throw ContractRefusal(
                "The relevance judge returned " + scores.Count + " scores for " + candidates.Count +
                " candidates; the port requires one score per candidate in order.",
                MemoryRelevanceJudgeErrorCodes.ScoreCountMismatch);
        }

        var checkedScores = new double[scores.Count];
        for (var index = 0; index < scores.Count; index++)
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

            checkedScores[index] = score;
        }

        return checkedScores;
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
        IReadOnlyList<double> scores,
        MemoryRelevanceJudgeSettings settings)
    {
        var admitted = new List<MemoryRelevanceJudgedCandidate>(candidates.Count);
        for (var index = 0; index < candidates.Count; index++)
        {
            if (scores[index] < settings.FloorScore)
            {
                continue;
            }

            admitted.Add(new MemoryRelevanceJudgedCandidate(candidates[index], scores[index]));
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
