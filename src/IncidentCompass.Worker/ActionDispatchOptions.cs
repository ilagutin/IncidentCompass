namespace IncidentCompass.Worker;

internal sealed class ActionDispatchOptions
{
    public const string SectionName = "IncidentCompass:ActionDispatch";

    public int BatchSize { get; set; } = 8;

    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// The adapter limit for an external action whose tool sets no <c>Tools.&lt;id&gt;.TimeoutSeconds</c>
    /// in the triage configuration. A per-tool value replaces it for that tool and is not capped by
    /// it. The claim deadline is the resolved limit plus a 30-second recovery grace.
    /// </summary>
    public int AdapterTimeoutSeconds { get; set; } = 30;
}
