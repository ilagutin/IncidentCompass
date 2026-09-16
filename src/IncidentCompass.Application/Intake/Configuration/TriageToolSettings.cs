namespace IncidentCompass.Application.Intake.Configuration;

/// <summary>
/// One configured tool. <see cref="TimeoutSeconds"/> is the tool's own execution limit and applies to
/// both kinds: an immediate read tool is bounded by the worker executor, and an external action is
/// bounded by the approved-action dispatcher. When it is absent an immediate tool gets
/// <see cref="DefaultImmediateTimeoutSeconds"/> and an external action gets the Worker host's
/// <c>ActionDispatch:AdapterTimeoutSeconds</c>. The key is optional so a configuration that does not
/// set it keeps its content and therefore its configuration hash.
/// </summary>
public sealed record TriageToolSettings(
    string Kind,
    string? EmbeddingRouteId,
    int? TopK,
    double? MinScore,
    string? Category = null,
    string? LogicalTargetId = null,
    string? Mode = null,
    int? TimeoutSeconds = null)
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
