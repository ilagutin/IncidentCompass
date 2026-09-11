namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The Application-owned half of the closed vocabulary a publication preparation reports.
/// </summary>
/// <remarks>
/// <para>
/// These sit beside <see cref="RemediationCodes" /> for the same reason those sit beside the
/// <c>source_workspace_*</c> and <c>source_patch_*</c> families: the adapter behind
/// <see cref="IRemediationWorkspace" /> returns its own codes unchanged, including the
/// <c>git_base_*</c> codes a correspondence refusal produces, because a caller that translated them
/// would either lose the reason or invent a second name for it. Every code in every family is a fixed
/// string carrying no path, no line and no byte of a file, so all of them are safe to log and persist.
/// </para>
/// <para>
/// Nothing here is a status a model chooses. A model cannot reach this path: the whole input is an
/// approved payload the backend froze and a remote listing the backend read.
/// </para>
/// </remarks>
public static class RemediationPublicationCodes
{
    /// <summary>
    /// The base was proved against the remote commit, the approved diff re-applied to it, and the
    /// files a push may carry were read back.
    /// </summary>
    public const string Prepared = "remediation_publication_prepared";

    /// <summary>
    /// The approved diff applied, but the tree it produced is not the tree the approval named, so
    /// the bytes a push would carry are not the approved ones.
    /// </summary>
    public const string ResultMismatch = "remediation_publication_result_mismatch";

    /// <summary>
    /// A file the approved diff writes could not be read back out of the patched copy. The diff
    /// applied and was verified, so this is a filesystem fault rather than anything about the change.
    /// </summary>
    public const string ChangedFileUnreadable = "remediation_publication_file_unreadable";

    /// <summary>
    /// A file the approved diff writes is larger than a single push request may carry.
    /// </summary>
    public const string ChangedFileTooLarge = "remediation_publication_file_too_large";
}
