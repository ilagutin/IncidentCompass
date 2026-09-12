namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The closed outcome vocabulary of patch parsing, validation and application. Codes sit beside the
/// <c>source_workspace_*</c> codes because they describe the same workspace, and a caller that
/// renders one renders these.
/// </summary>
/// <remarks>
/// Every refusal is a code and nothing else. No code carries a path, a line, a byte of the patch or a
/// byte of a file, because a refusal is surfaced to callers that log and persist it, and the patch is
/// model text about attacker-influenced incident data. A reviewer who needs to know which rule fired
/// gets the rule; a reviewer who needs to know what the text said reads the patch itself, which is
/// held where patches are held rather than in a log line.
/// </remarks>
internal static class SourcePatchCodes
{
    /// <summary>The text parsed as the accepted subset and passed every check off the filesystem.</summary>
    public const string Parsed = "source_patch_parsed";

    /// <summary>The whole patch applied and the workspace now holds the result.</summary>
    public const string Applied = "source_patch_applied";

    /// <summary>The patch text is blank, or describes no file.</summary>
    public const string Empty = "source_patch_empty";

    /// <summary>The patch is larger than the action-payload ceiling admits. See <see cref="SourcePatchLimits"/>.</summary>
    public const string TooLarge = "source_patch_too_large";

    /// <summary>The patch does not parse as the accepted unified-diff subset.</summary>
    public const string Malformed = "source_patch_malformed";

    /// <summary>A binary hunk, or a header saying the files differ in binary.</summary>
    public const string Binary = "source_patch_binary";

    /// <summary>A rename, a copy, a mode change, or another operation the format allows and this does not.</summary>
    public const string UnsupportedOperation = "source_patch_unsupported_operation";

    /// <summary>A path that escapes the workspace, names a device, or is otherwise inadmissible.</summary>
    public const string PathRejected = "source_patch_path_rejected";

    /// <summary>A path that names a credential or key material.</summary>
    public const string SecretPathRejected = "source_patch_secret_path_rejected";

    /// <summary>A path whose extension the source-read boundary would refuse to read back.</summary>
    public const string ExtensionRejected = "source_patch_extension_rejected";

    /// <summary>Two file sections name the same path.</summary>
    public const string DuplicatePath = "source_patch_duplicate_path";

    /// <summary>
    /// One section's path is a directory prefix of another's, so the patch asks for one name to be
    /// both a file and a directory.
    /// </summary>
    public const string PathConflict = "source_patch_path_conflict";

    /// <summary>
    /// The patch text holds an unpaired surrogate, so part of it denotes no character and no file
    /// could hold it.
    /// </summary>
    public const string Unencodable = "source_patch_unencodable";

    /// <summary>The patch changes more files than the bound admits.</summary>
    public const string FileLimit = "source_patch_file_limit";

    /// <summary>One file section carries more hunks than the bound admits.</summary>
    public const string HunkLimit = "source_patch_hunk_limit";

    /// <summary>A file section carries no hunk at all.</summary>
    public const string NoHunks = "source_patch_no_hunks";

    /// <summary>A hunk body holds a different number of lines than its header declares.</summary>
    public const string HunkCountMismatch = "source_patch_hunk_count_mismatch";

    /// <summary>Two hunks of one file cover the same base lines, or run backwards.</summary>
    public const string HunkOverlap = "source_patch_hunk_overlap";

    /// <summary>A context or removed line does not match the file the workspace holds.</summary>
    public const string ContextMismatch = "source_patch_context_mismatch";

    /// <summary>A modified or deleted file is not in the workspace.</summary>
    public const string TargetMissing = "source_patch_target_missing";

    /// <summary>A created file is already in the workspace.</summary>
    public const string TargetExists = "source_patch_target_exists";

    /// <summary>A target is not decodable text, so it has no lines a hunk could address.</summary>
    public const string TargetBinary = "source_patch_target_binary";

    /// <summary>A target is larger before or after the change than the read boundary admits.</summary>
    public const string TargetTooLarge = "source_patch_target_too_large";

    /// <summary>A symlink, junction or other reparse point sits on the path to a target.</summary>
    public const string LinkRejected = "source_patch_link_rejected";

    /// <summary>
    /// A path segment differs in case from the name the workspace holds. Refused on every platform,
    /// because Windows would resolve it to the entry that is already there and Linux would treat it
    /// as a different name.
    /// </summary>
    public const string PathCaseMismatch = "source_patch_path_case_mismatch";

    /// <summary>
    /// A filesystem error ended the attempt and the workspace was put back as it was. Nothing the
    /// patch described survives. No host path reaches the caller.
    /// </summary>
    public const string Unavailable = "source_patch_unavailable";

    /// <summary>
    /// A filesystem error ended the attempt and the workspace could not be put back as it was. The
    /// patch was not applied, but the workspace no longer holds what it held and must be discarded
    /// rather than read, identified or reused. No host path reaches the caller.
    /// </summary>
    public const string RollbackFailed = "source_patch_rollback_failed";
}
