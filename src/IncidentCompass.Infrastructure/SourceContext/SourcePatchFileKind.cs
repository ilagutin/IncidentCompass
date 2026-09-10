namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// What one file section of a patch does. These three are the whole supported set: a rename, a copy
/// and a mode change are refused rather than represented, so no value here can stand for one.
/// </summary>
internal enum SourcePatchFileKind
{
    /// <summary>The file exists in the base and its content changes.</summary>
    Modify,

    /// <summary>The file is absent from the base and the patch supplies all of its content.</summary>
    Create,

    /// <summary>The file exists in the base, the patch quotes all of it, and it is removed.</summary>
    Delete,
}
