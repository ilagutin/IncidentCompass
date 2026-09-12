namespace IncidentCompass.Infrastructure.SourceContext;

public sealed class SourceContextOptions
{
    public const string SectionName = "IncidentCompass:SourceContext";

    public int MaxFrames { get; set; } = 8;

    public int MaxCandidateFiles { get; set; } = 256;

    public int MaxSourceBytes { get; set; } = 256 * 1024;

    public int MaxExcerptLines { get; set; } = 21;

    public string[] AllowedExtensions { get; set; } = [".cs"];

    public SourceContextRootOptions[] Roots { get; set; } = [];

    /// <summary>
    /// Absolute path of the directory disposable remediation workspaces are created below. Unset
    /// means remediation is not configured on this host and every remediation call refuses.
    /// </summary>
    /// <remarks>
    /// There is deliberately no default. A default would make the first host to gain a monitored
    /// root start writing copies of it somewhere nobody chose, sized by a bound nobody agreed to,
    /// onto whatever filesystem the process happened to have. Declaring the location is how an
    /// operator says the host has room for it and where leftovers of a killed process can be found.
    /// It must not be the monitored root or sit below it; that is refused when a workspace is
    /// created rather than here, because it depends on resolving both paths as links.
    /// </remarks>
    public string? WorkspaceRoot { get; set; }
}
