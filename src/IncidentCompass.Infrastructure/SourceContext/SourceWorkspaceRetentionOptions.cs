namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// How old a leftover remediation workspace has to be before it is reaped, and how many one run may
/// delete.
/// </summary>
/// <remarks>
/// <para>
/// It is deliberately separate from <see cref="IncidentCompass.Application.Core.Configuration.RetentionOptions"/>.
/// That one is about rows: two windows counted in days and a row budget, all of them properties of
/// durable data that are the same wherever the operations are driven from. This one is about
/// directories on one host's disk, counted in hours, and only a host that configures
/// <see cref="SourceContextOptions.WorkspaceRoot"/> has any. Folding hours-scale directory retention
/// into a days-scale row policy would make one setting answer two questions with different units and
/// different owners.
/// </para>
/// <para>
/// There is no <c>Enabled</c> flag, because there is already a switch and a second one could
/// disagree with it. A host with no workspace root writes no workspace, so there is nothing to reap;
/// a host with one writes them and must reap them. Turning the reaper off while workspaces are still
/// being written is not a state worth being able to configure.
/// </para>
/// </remarks>
public sealed class SourceWorkspaceRetentionOptions
{
    public const string SectionName = "IncidentCompass:SourceWorkspaceRetention";

    /// <summary>
    /// Hours a prefixed directory under the workspace root must have existed, and have been
    /// untouched for, before a run may delete it.
    /// </summary>
    /// <remarks>
    /// Six hours rather than minutes, and the headroom is the whole point. A workspace never
    /// outlives one call to <c>IRemediationWorkspace</c>: the adapter materializes a copy, reads or
    /// patches it, and disposes it before returning, so no workspace is alive across the model call
    /// between those two calls. The longest a live workspace can therefore exist is one bounded tree
    /// copy plus one bounded patch apply, and <see cref="SourceWorkspaceBounds.Default"/> caps that
    /// at 20,000 files and 128 MB, which is seconds on any disk a host would run this on and minutes
    /// on a pathological one. Six hours is that bound with three orders of magnitude of room, and
    /// the only cost of the room is disk under a directory the operator chose.
    /// </remarks>
    public int RetentionHours { get; set; } = 6;

    /// <summary>
    /// Upper bound on directories one run may delete. It bounds the recursive deletion, which is the
    /// expensive half, and a run stops enumerating as soon as the budget is spent.
    /// </summary>
    /// <remarks>
    /// It does not bound how many directories a run reads: a run that finds only fresh or unreadable
    /// names walks the whole listing without spending any budget. That is one <c>readdir</c> of one
    /// directory, not a table scan, so unlike the row retention operations the read cost here does
    /// not grow with a backlog. A backlog drains at this budget divided by the retention interval.
    /// </remarks>
    public int MaxDirectoriesPerRun { get; set; } = 64;
}
