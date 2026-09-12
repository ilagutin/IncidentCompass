namespace IncidentCompass.Application.Observability.CostRollup;

/// <remarks>
/// <see cref="SpendCoverageStatement"/> exists because <see cref="Hours"/> can look complete when it
/// is not: an embedding call writes no <c>ModelCall</c> ledger row at all, so it never reaches any
/// count or total there, no matter which adapter serves the call. A caller that never opens
/// <c>docs/cost-tracking.md</c> still deserves to know that the spend totals here are a lower bound
/// on chat-completion spend rather than a total across every call kind.
/// </remarks>
/// <param name="FromUtc">The inclusive UTC start of the requested window.</param>
/// <param name="ToUtc">The exclusive UTC end of the requested window.</param>
/// <param name="Hours">Per-hour usage and spend totals covering the requested window.</param>
/// <param name="SpendCoverageStatement">A short restatement of what the spend totals in this response cover and what they structurally exclude.</param>
public sealed record CostRollupResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    IReadOnlyList<CostRollupHour> Hours,
    string SpendCoverageStatement)
{
    /// <summary>
    /// The fixed value of <see cref="SpendCoverageStatement"/>. An embedding call writes no
    /// <c>ModelCall</c> ledger row, so it is absent from every count and token total this rollup
    /// produces, not merely unpriced, and no pricing row could bring it into a spend figure. This
    /// stays true regardless of which adapter serves the call, including an in-process one that
    /// charges nothing.
    /// </summary>
    public const string EmbeddingCallsExcludedNotice =
        "The spend totals in this response are a lower bound on chat-completion spend for the " +
        "window. An embedding call, whichever adapter serves it, writes no ModelCall ledger row, so " +
        "it is absent from the call counts, the token totals and the spend totals here, not merely " +
        "unpriced. This system does not record what an embedding call cost, whoever or whatever " +
        "served it: if the deployment's embedding adapter bills at all, that cost is knowable only " +
        "wherever that adapter's own billing lives.";
}
