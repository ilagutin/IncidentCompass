namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The closed outcome vocabulary of source-workspace materialization. Codes sit beside the
/// <c>source_*</c> codes the source-lookup path already returns, because both describe the same
/// monitored checkout from the same host options; a caller that already renders one renders these.
/// </summary>
internal static class SourceWorkspaceCodes
{
    /// <summary>A workspace was materialized and its tree identity computed.</summary>
    public const string Created = "source_workspace_created";

    /// <summary>The configured monitored root is missing or is not a directory.</summary>
    public const string RootUnavailable = "source_root_unavailable";

    /// <summary>The configured workspace root is the monitored root or sits below it.</summary>
    public const string WorkspaceRootRejected = "source_workspace_root_rejected";

    /// <summary>A symlink, junction or other reparse point was found on some path segment.</summary>
    public const string LinkRejected = "source_workspace_link_rejected";

    /// <summary>A nested repository marker or a submodule declaration was found.</summary>
    public const string SubmoduleRejected = "source_workspace_submodule_rejected";

    /// <summary>An enumerated entry resolved outside the canonical monitored root.</summary>
    public const string PathRejected = "source_workspace_path_rejected";

    /// <summary>The tree holds more files than the configured bound admits.</summary>
    public const string FileLimit = "source_workspace_file_limit";

    /// <summary>The tree holds more bytes than the configured bound admits.</summary>
    public const string SizeLimit = "source_workspace_size_limit";

    /// <summary>The tree nests deeper than the configured bound admits.</summary>
    public const string DepthLimit = "source_workspace_depth_limit";

    /// <summary>A filesystem error ended the materialization. No host path reaches the caller.</summary>
    public const string Unavailable = "source_workspace_unavailable";
}
