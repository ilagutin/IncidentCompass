namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The closed vocabulary of comparing an approved local base against one remote commit's tree.
/// </summary>
/// <remarks>
/// Every value is a fixed string. None carries a path, a byte of a file or a host location, because
/// callers log and persist these and the tree being described is a production checkout.
/// </remarks>
internal static class GitTreeCorrespondenceCodes
{
    /// <summary>
    /// Every path the two sides share holds identical bytes, every remote path is present locally,
    /// and what is left is local-only and excluded from the push.
    /// </summary>
    public const string Proved = "git_base_proved";

    /// <summary>
    /// A path the two sides share holds different bytes. The checkout has moved, or it is dirty, and
    /// a push built on this remote commit would not be the approved base plus the approved diff.
    /// </summary>
    public const string ContentDiverged = "git_base_content_diverged";

    /// <summary>
    /// The remote tree holds a path the approved local base does not. Refused rather than accepted
    /// as a narrower intersection: the pushed tree keeps that path, so the commit would carry
    /// content the approved base never described.
    /// </summary>
    public const string PathMissingLocally = "git_base_path_missing_locally";

    /// <summary>The remote base tree describes nothing, so there is nothing to prove against.</summary>
    public const string RemoteTreeEmpty = "git_base_remote_tree_empty";
}
