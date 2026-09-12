using IncidentCompass.Application.Remediation;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Turns the files an approved diff wrote into the overlay a push lays over the base commit's tree.
/// </summary>
/// <remarks>
/// The only decision here is the file mode, and it is made by looking, not by choosing: a path the
/// base tree already holds keeps the mode the base gave it, and a path the diff creates gets the
/// ordinary non-executable mode. Defaulting an existing path to non-executable would silently drop the
/// executable bit off a script, and defaulting a new one to executable would grant a bit nobody asked
/// for, so neither direction is guessed.
/// </remarks>
internal static class BranchPushTreeEntries
{
    public static IReadOnlyList<CodePublicationTreeEntry> Build(
        IReadOnlyList<RemediationPublicationFile> files,
        IReadOnlyDictionary<string, string> baseFileModes) =>
        files
            .Select(file => new CodePublicationTreeEntry(
                file.RepositoryPath,
                baseFileModes.TryGetValue(file.RepositoryPath, out var mode)
                    ? mode
                    : GitHubGitDataRequests.RegularFileMode,
                file.Content))
            .ToArray();
}
