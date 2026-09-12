namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The single admission decision for one path a patch names. It is a pure function of the text and
/// the limits, so every refusal below can be asserted directly rather than through a filesystem that
/// would answer differently on Windows and on Linux.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is textual and total.</b> A patch is model text, and a model can be steered by the
/// incident data it was shown, so the path in a diff header is attacker-influenced input. This
/// refuses on the shape of the text before any path is combined with a root, because combining first
/// and checking afterwards means the check has to out-guess two operating systems' path parsers. The
/// applier still rechecks containment after canonicalization; that is a second boundary, not this
/// one.
/// </para>
/// <para>
/// <b>The rule is an allowlist wearing a denylist's clothes.</b> What survives is a relative path of
/// <c>/</c>-separated segments made of printable ASCII, no deeper than the tree walk admits, no
/// segment being empty, <c>.</c>, <c>..</c>, a reserved device name, a git marker or a name that
/// Windows would silently rewrite, with an admitted extension and no credential shape. Everything
/// else is refused. The denylist entries below exist because each has a specific escape behind it,
/// not because the list is the mechanism.
/// </para>
/// </remarks>
internal static class SourcePatchPathPolicy
{
    /// <summary>
    /// Names Windows resolves as devices whatever directory they appear in, with or without an
    /// extension. Refused on every platform, because a patch that is admitted on Linux and refused
    /// on Windows is a patch whose meaning depends on where the worker happens to run.
    /// </summary>
    private static readonly string[] ReservedDeviceNames =
    [
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    ];

    /// <summary>
    /// Directories whose contents are credentials by convention rather than by extension.
    /// </summary>
    private static readonly string[] SecretDirectoryNames = [".ssh", ".aws", ".gnupg", ".docker"];

    /// <summary>
    /// File names that are credentials whatever directory they sit in.
    /// </summary>
    private static readonly string[] SecretFileNames =
    [
        ".env", ".netrc", "_netrc", ".npmrc", ".pgpass", ".git-credentials", "credentials",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
    ];

    /// <summary>
    /// Names the tree walk already refuses to copy, refused again here so a patch cannot introduce
    /// what a materialization would not admit.
    /// </summary>
    private static readonly string[] GitMarkerNames = [".git", ".gitmodules"];

    /// <summary>
    /// Extensions that carry key material. The list is short and specific: it is not a general
    /// secret detector, and it is not the reason a patch is safe.
    /// </summary>
    private static readonly string[] SecretExtensions =
    [
        ".pem", ".key", ".pfx", ".p12", ".cer", ".crt", ".der", ".jks", ".keystore", ".ppk", ".asc",
    ];

    /// <summary>
    /// Returns the refusal code for a path a patch must not name, or <c>null</c> to admit it.
    /// </summary>
    public static string? Reject(string repositoryPath, SourcePatchLimits limits)
    {
        if (string.IsNullOrEmpty(repositoryPath) || repositoryPath.Length > limits.MaximumPathCharacters)
        {
            return SourcePatchCodes.PathRejected;
        }

        var characterRefusal = RejectCharacters(repositoryPath);
        if (characterRefusal is not null)
        {
            return characterRefusal;
        }

        var segments = repositoryPath.Split('/');
        if (segments.Length > limits.MaximumPathSegments)
        {
            // The depth a path names is a property of its text, so it is refused here rather than
            // by the walk that recomputes the tree identity after the write. Leaving it to the walk
            // meant a file was created, the walk refused the tree it produced, and the whole attempt
            // was rolled back to report a workspace code for what was only ever a path this should
            // not have admitted.
            return SourcePatchCodes.PathRejected;
        }

        foreach (var segment in segments)
        {
            var segmentRefusal = RejectSegment(segment);
            if (segmentRefusal is not null)
            {
                return segmentRefusal;
            }
        }

        var name = segments[^1];
        if (IsSecret(segments, name))
        {
            return SourcePatchCodes.SecretPathRejected;
        }

        var extension = Path.GetExtension(name);
        return limits.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? null
            : SourcePatchCodes.ExtensionRejected;
    }

    /// <summary>
    /// Refuses on characters before the path is ever split, because several of them change what a
    /// split even means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Printable ASCII and nothing else.</b> The reason a path is checked at all is that a human
    /// approves a diff by reading it, so the path a reviewer reads must be the path a filesystem
    /// opens, and the two must be the same string for everyone who reads it. Everything outside
    /// <c>U+0021</c> to <c>U+007E</c> breaks one of those. A NUL truncates the path for any API that
    /// reaches a C string. Control characters and spaces move or hide what follows them. Format
    /// characters, which <see cref="char.IsControl(char)"/> does not cover, are worse than
    /// invisible: a right-to-left override reverses the segment a reviewer sees while leaving the
    /// bytes that are opened untouched, and a zero-width space or a byte-order mark is a segment
    /// boundary nobody can see. Fullwidth forms are homoglyphs of the separator, the drive separator
    /// and the escape, so a segment holding U+FF0F reads as two segments and is one. Refusing the
    /// whole range refuses the next homoglyph too, at the cost of a repository whose file names are
    /// not ASCII, which this cannot patch and says so.
    /// </para>
    /// <para>
    /// <b>The ASCII characters that are still refused.</b> A backslash is a separator on Windows and
    /// an ordinary character on Linux, so a path carrying one means two different trees; refusing it
    /// also disposes of <c>a\..\..\etc</c> without having to reason about it. A colon opens an NTFS
    /// alternate data stream and introduces a drive letter, so <c>x.cs:hidden</c> and <c>c:/x</c>
    /// both end here. The rest are the characters Windows rejects in a file name, refused on every
    /// platform for the reason the device names are: a name Linux accepts and Windows does not is a
    /// path whose meaning depends on where the worker happens to run. A leading slash is absolute,
    /// and a doubled leading slash is a UNC prefix that names another host.
    /// </para>
    /// <para>
    /// <b>Body lines are deliberately not filtered this way.</b> See
    /// <see cref="SourcePatchParser"/>.
    /// </para>
    /// </remarks>
    private static string? RejectCharacters(string repositoryPath)
    {
        foreach (var character in repositoryPath)
        {
            if (character is < '!' or > '~' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|')
            {
                return SourcePatchCodes.PathRejected;
            }
        }

        return repositoryPath[0] == '/' || Path.IsPathRooted(repositoryPath)
            ? SourcePatchCodes.PathRejected
            : null;
    }

    /// <remarks>
    /// An empty segment is a doubled separator or a trailing one, neither of which names a file. A
    /// <c>.</c> or <c>..</c> segment is traversal, refused here by name rather than by canonicalizing
    /// and hoping the result stayed inside. A trailing dot or space is stripped by Windows when the
    /// path is opened, so <c>secret.cs.</c> and <c>secret.cs</c> are the same file there and two
    /// different files here; refusing the rewritable form keeps the path a reviewer reads and the
    /// path a filesystem opens the same string. A <c>.git</c> or <c>.gitmodules</c> segment is
    /// refused for the reason the tree walk refuses it: a patch must not write repository metadata,
    /// declare a submodule, or reach into a nested repository the workspace already refused to copy.
    /// </remarks>
    private static string? RejectSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or "..")
        {
            return SourcePatchCodes.PathRejected;
        }

        if (segment[^1] == '.')
        {
            return SourcePatchCodes.PathRejected;
        }

        if (GitMarkerNames.Contains(segment, StringComparer.OrdinalIgnoreCase))
        {
            return SourcePatchCodes.PathRejected;
        }

        var withoutExtension = segment.Split('.')[0];
        return ReservedDeviceNames.Contains(withoutExtension, StringComparer.OrdinalIgnoreCase)
            ? SourcePatchCodes.PathRejected
            : null;
    }

    private static bool IsSecret(string[] segments, string name)
    {
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (SecretDirectoryNames.Contains(segments[index], StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (SecretFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // `.env.production` and `id_rsa.bak` are the same secret with a suffix.
        foreach (var secret in SecretFileNames)
        {
            if (name.StartsWith(secret + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return SecretExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }
}
