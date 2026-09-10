namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The single admission decision for one enumerated entry. It is a pure function of the attributes,
/// the name and the depth so the refusals it encodes can be asserted directly, without a test having
/// to create a symlink first: on a developer machine that cannot create one, a filesystem-level test
/// silently proves nothing.
/// </summary>
/// <remarks>
/// Name matching is case-insensitive on every platform. The alternative, matching the case rules of
/// the running filesystem, would let the same tree be admitted on Linux and refused on Windows;
/// failing closed on a case variant costs a visible refusal an operator can read, and is the safer
/// direction for a rule whose whole job is to keep a nested repository out.
/// </remarks>
internal static class SourceWorkspaceEntryPolicy
{
    private const string GitDirectoryName = ".git";
    private const string GitModulesFileName = ".gitmodules";

    /// <summary>
    /// Returns the refusal code for an entry the workspace must not contain, or <c>null</c> to admit
    /// it. Root children are depth 1.
    /// </summary>
    /// <remarks>
    /// A reparse point is refused before anything else, on a file as well as a directory, because it
    /// is the only way an entry inside the root can name bytes outside it. A <c>.gitmodules</c> file
    /// at any depth refuses the tree, and so does a <c>.git</c> entry below depth 1. Both are
    /// heuristics stated as such: without git there is no submodule concept to consult, only the
    /// markers a submodule leaves behind, so this refuses a declared-but-absent submodule and a
    /// merely nested independent repository alike.
    /// </remarks>
    public static string? Reject(FileAttributes attributes, string name, int depth)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return SourceWorkspaceCodes.LinkRejected;
        }

        if (IsNamed(name, GitModulesFileName))
        {
            return SourceWorkspaceCodes.SubmoduleRejected;
        }

        return depth > 1 && IsNamed(name, GitDirectoryName)
            ? SourceWorkspaceCodes.SubmoduleRejected
            : null;
    }

    /// <summary>
    /// True for an entry that is left out of the copy rather than refused: the monitored root's own
    /// <c>.git</c>. It is history and index rather than working-tree content, it is the largest
    /// thing in most checkouts, and copying it would carry packed object data into a workspace that
    /// has no use for it. The tree identity therefore describes the working tree, not the repository.
    /// </summary>
    public static bool IsExcluded(string name, int depth) => depth == 1 && IsNamed(name, GitDirectoryName);

    private static bool IsNamed(string name, string reserved) =>
        string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase);
}
