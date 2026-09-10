namespace IncidentCompass.Worker;

/// <summary>
/// Whether this host runs the two retention operations, and how often. It is deliberately separate
/// from <see cref="IncidentCompass.Application.Core.Configuration.RetentionOptions" />: that one says
/// how old a payload has to be and how many rows a single run may touch, which are properties of the
/// data and are the same wherever the operations are driven from. This one is a property of the
/// Worker, and only the Worker binds it.
/// </summary>
internal sealed class RetentionScheduleOptions
{
    public const string SectionName = "IncidentCompass:RetentionSchedule";

    /// <summary>
    /// Whether the Worker drives retention at all. Setting it to <c>false</c> is the supported way to
    /// stop both operations: the hosted service is still composed and still starts, it simply runs
    /// neither operation and says so once, so an operator can tell "retention is switched off" from
    /// "retention is broken" by reading the startup log. Nothing is deleted or emptied while it is
    /// off, and turning it back on resumes from whatever backlog accumulated meanwhile.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Minutes between the end of one retention pass and the start of the next. Minutes rather than
    /// seconds because this is maintenance, not a queue poll: the neighbouring workers measure their
    /// intervals in seconds because a job waiting to be claimed is latency the user feels, while a
    /// payload waiting to be emptied is not.
    /// </summary>
    public int IntervalMinutes { get; set; } = 15;
}
