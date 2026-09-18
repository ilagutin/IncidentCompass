namespace IncidentCompass.Application.Intake.Configuration;

/// <summary>
/// One configured tool. <see cref="TimeoutSeconds"/> is the tool's own execution limit and applies to
/// both kinds: an immediate read tool is bounded by the worker executor, and an external action is
/// bounded by the approved-action dispatcher. When it is absent an immediate tool gets
/// <see cref="DefaultImmediateTimeoutSeconds"/> and an external action gets the Worker host's
/// <c>ActionDispatch:AdapterTimeoutSeconds</c>. The key is optional so a configuration that does not
/// set it keeps its content and therefore its configuration hash.
/// <para>
/// <see cref="VectorOnlyFallback"/> belongs to <c>memory_search</c> alone and is refused on any other
/// tool. It names what that tool does when lexical coverage leaves no candidate: <c>off</c>,
/// <c>foreign_script</c> or <c>always</c>. It is optional for the same reason the timeout is, and an
/// absent key resolves to <c>foreign_script</c>.
/// </para>
/// <para>
/// <see cref="RelevanceJudge"/>, <see cref="RelevanceConfirmScore"/> and
/// <see cref="RelevanceFloorScore"/> belong to <c>memory_search</c> alone and are refused on any other
/// tool. The first names whether that tool asks a relevance judge about a query, <c>off</c> or
/// <c>on</c>; the other two are the judge scores at or above which a candidate is confirmed and below
/// which it is dropped. All three are optional for the same reason the timeout is, and their defaults
/// live in <c>MemoryRelevanceJudgeSetting</c>.
/// </para>
/// </summary>
public sealed record TriageToolSettings(
    string Kind,
    string? EmbeddingRouteId,
    int? TopK,
    double? MinScore,
    string? Category = null,
    string? LogicalTargetId = null,
    string? Mode = null,
    int? TimeoutSeconds = null,
    string? VectorOnlyFallback = null,
    string? RelevanceJudge = null,
    double? RelevanceConfirmScore = null,
    double? RelevanceFloorScore = null)
{
    public const int MinimumTimeoutSeconds = 1;

    public const int MaximumTimeoutSeconds = 3600;

    public const int DefaultImmediateTimeoutSeconds = 120;

    /// <summary>The execution limit an immediate read tool runs under.</summary>
    public TimeSpan ResolveImmediateTimeout() =>
        TimeSpan.FromSeconds(TimeoutSeconds ?? DefaultImmediateTimeoutSeconds);

    /// <summary>
    /// The adapter limit an external action runs under: its own value when configured, otherwise the
    /// host-wide default the Worker passes in.
    /// </summary>
    public TimeSpan ResolveActionTimeout(TimeSpan hostDefault) =>
        TimeoutSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : hostDefault;
}
