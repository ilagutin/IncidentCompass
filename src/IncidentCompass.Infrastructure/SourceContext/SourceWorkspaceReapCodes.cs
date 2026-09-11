namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The closed outcome vocabulary of one abandoned-workspace reaping run. Every value is a fixed
/// string carrying no host path, so it is safe to log and safe to surface.
/// </summary>
public static class SourceWorkspaceReapCodes
{
    /// <summary>
    /// No workspace root is configured on this host, so nothing writes workspaces and there is
    /// nothing to reap. This is the shipped state, not a misconfiguration.
    /// </summary>
    public const string NotConfigured = "source_workspace_reap_not_configured";

    /// <summary>
    /// A workspace root is configured but does not exist yet. Materialization creates it on first
    /// use, so before a first remediation pass this is the normal state rather than an error.
    /// </summary>
    public const string RootAbsent = "source_workspace_reap_root_absent";

    /// <summary>The run finished. Its counters say what it examined, deleted and left alone.</summary>
    public const string Completed = "source_workspace_reap_completed";

    /// <summary>
    /// The workspace root could not be listed. Whatever is under it is left alone and the next run
    /// tries again; no host path reaches the caller.
    /// </summary>
    public const string Unavailable = "source_workspace_reap_unavailable";
}
