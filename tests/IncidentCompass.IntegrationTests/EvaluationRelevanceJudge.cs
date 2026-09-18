using IncidentCompass.Application.Memory;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// A deterministic relevance judge for the triage evaluation host, the judge counterpart of
/// <see cref="EvaluationScriptedModelClient" />: it answers from a fixed table instead of a model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the host composes a judge at all.</b> The default product path runs one, and
/// <c>memory_search</c> confirms a document only through a relevance judge, against the fault query
/// built from the trigger signal. A judge-less host confirms nothing, so without a judge the known and
/// stale cases could never reach a memory-based known-incident report. That is the documented cost of a
/// judge-less host, not the path this workflow test exists to exercise.
/// </para>
/// <para>
/// <b>What it does not test.</b> This double covers the plumbing: that a judged call admits, confirms
/// and publishes end to end. It does not test the defence against a role choosing its query to raise a
/// band, and cannot, because its role-query table and its fault table map to the same chunks, so it
/// would confirm the same documents under the rule this release replaced. The defence is covered by
/// <c>MemorySearchFaultConfirmationTests.ExecuteAsync_ADocumentWhoseTextIsTheQueryButNotTheFaultIsAdmittedAndBandedLow</c>
/// and by the attack leg of <c>MemorySearchRelevanceJudgeBenchmarkTests</c>.
/// </para>
/// <para>
/// <b>Where the table comes from.</b> Nothing in it is lexical overlap, which would recreate the rule
/// that cannot confirm these cases. Each entry is a relevance statement the repository already makes by
/// hand:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A role query the scripted memory worker sends is a labelled query of the shared retrieval benchmark,
/// and the chunks relevant to it are the ones that benchmark labels for it. Nothing else is relevant.
/// </description></item>
/// <item><description>
/// A fault is relevant to exactly one document: the one the evaluation case is about, which is the
/// labelled answer of the benchmark query the scripted worker issues for that case. The known checkout
/// timeout is the known incident about checkout inventory timeouts, and the stale checkout runbook case
/// is the legacy inventory latency runbook. A fault is identified by its own error message, which the
/// fault query always carries.
/// </description></item>
/// </list>
/// <para>
/// Every other pair scores below the shipped floor, so the judge confirms only what the table states and
/// admits nothing else. A case the table does not name, the unknown and insufficient ones among them,
/// gets nothing.
/// </para>
/// </remarks>
internal sealed class EvaluationRelevanceJudge(
    IReadOnlyDictionary<string, IReadOnlySet<string>> relevantByRoleQuery,
    IReadOnlyDictionary<string, IReadOnlySet<string>> relevantByFaultMessage) : IMemoryRelevanceJudge
{
    /// <summary>Well above the shipped confirm score.</summary>
    public const float Relevant = 4.0f;

    /// <summary>Well below the shipped floor, so the candidate is dropped.</summary>
    public const float NotRelevant = -4.0f;

    public Task<IReadOnlyList<float>> ScoreAsync(
        string query,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        var relevant = RelevantTo(query);
        return Task.FromResult<IReadOnlyList<float>>(candidates
            .Select(candidate => relevant.Contains(candidate) ? Relevant : NotRelevant)
            .ToArray());
    }

    /// <summary>
    /// A role query is matched exactly; a fault query is matched by the case's error message, which it
    /// contains verbatim. The two tables never overlap, because no role query carries a case's message.
    /// </summary>
    private IReadOnlySet<string> RelevantTo(string query)
    {
        if (relevantByRoleQuery.TryGetValue(query, out var byRoleQuery))
        {
            return byRoleQuery;
        }

        foreach (var (message, chunks) in relevantByFaultMessage)
        {
            if (query.Contains(message, StringComparison.Ordinal))
            {
                return chunks;
            }
        }

        return new HashSet<string>(StringComparer.Ordinal);
    }
}
