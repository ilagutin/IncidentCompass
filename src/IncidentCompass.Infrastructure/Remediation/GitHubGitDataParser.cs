using System.Net;
using System.Text.Json;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Reads the provider's answers, and refuses anything that is not exactly the shape expected.
/// </summary>
/// <remarks>
/// Every value this returns is either a git object name, a repository path or a file mode, and each
/// one is checked against a closed shape before it leaves: an object name is forty lower-hex
/// characters, a mode is one of two constants, and a path is whatever the provider said, used only as
/// a dictionary key and never as a filesystem path. Nothing here forwards a provider message, a URL or
/// a header into a code, a log line or a result.
/// </remarks>
internal static class GitHubGitDataParser
{
    private const string TreeType = "tree";

    public static string? TryReadObjectSha(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("object", out var target) &&
        target.ValueKind == JsonValueKind.Object
            ? TryReadSha(target)
            : null;

    public static string? TryReadSha(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("sha", out var sha) &&
        sha.ValueKind == JsonValueKind.String &&
        GitBlobIdentity.IsValid(sha.GetString())
            ? sha.GetString()
            : null;

    /// <summary>Reads a commit's own name, its tree and its single parent.</summary>
    public static (string? CommitSha, string? TreeSha, string? ParentSha) TryReadCommit(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            TryReadSha(root) is not { } commitSha ||
            !root.TryGetProperty("tree", out var tree) ||
            TryReadSha(tree) is not { } treeSha)
        {
            return (null, null, null);
        }

        string? parent = null;
        if (root.TryGetProperty("parents", out var parents) && parents.ValueKind == JsonValueKind.Array)
        {
            var materialized = parents.EnumerateArray().Take(2).ToArray();
            parent = materialized.Length == 1 ? TryReadSha(materialized[0]) : null;
        }

        return (commitSha, treeSha, parent);
    }

    /// <summary>
    /// Reads a recursive tree listing into path-to-blob and path-to-mode maps, or refuses.
    /// </summary>
    /// <remarks>
    /// Truncation refuses rather than narrowing: a comparison against a subset the provider chose
    /// would prove nothing about the paths it left out. A non-regular entry - a symlink, a submodule,
    /// anything with another mode - refuses too, because a push builds on this tree and would carry
    /// that entry into a commit this product cannot say anything about.
    /// </remarks>
    public static (IReadOnlyDictionary<string, string>? Blobs, IReadOnlyDictionary<string, string>? Modes, string? Code)
        TryReadTree(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("tree", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return (null, null, CodePublicationCodes.ResponseMalformed);
        }

        if (root.TryGetProperty("truncated", out var truncated) &&
            truncated.ValueKind == JsonValueKind.True)
        {
            return (null, null, CodePublicationCodes.BaseTreeTruncated);
        }

        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        var modes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String)
            {
                return (null, null, CodePublicationCodes.ResponseMalformed);
            }

            // A directory entry carries no content and has no local counterpart, so it is skipped
            // rather than refused. Everything that is not a directory must be a regular file: a
            // submodule arrives as a commit entry and a symlink as a blob with a link mode, and
            // neither is something this product can compare or reproduce.
            if (string.Equals(type.GetString(), TreeType, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(type.GetString(), GitHubGitDataRequests.BlobType, StringComparison.Ordinal) ||
                !IsRegularFileMode(mode.GetString()))
            {
                return (null, null, CodePublicationCodes.BaseTreeUnsupportedEntry);
            }

            if (TryReadSha(entry) is not { } blobSha || !blobs.TryAdd(path.GetString()!, blobSha))
            {
                return (null, null, CodePublicationCodes.ResponseMalformed);
            }

            modes[path.GetString()!] = mode.GetString()!;
        }

        return (blobs, modes, null);
    }

    public static string? MapFailure(HttpStatusCode statusCode) => statusCode switch
    {
        >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices => null,
        HttpStatusCode.Unauthorized => CodePublicationCodes.AuthenticationFailed,
        HttpStatusCode.Forbidden => CodePublicationCodes.Forbidden,
        HttpStatusCode.TooManyRequests => CodePublicationCodes.RateLimited,
        HttpStatusCode.NotFound => CodePublicationCodes.RepositoryNotFound,
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
            CodePublicationCodes.RequestInvalid,
        _ => CodePublicationCodes.Unavailable
    };

    private static bool IsRegularFileMode(string? mode) =>
        string.Equals(mode, GitHubGitDataRequests.RegularFileMode, StringComparison.Ordinal) ||
        string.Equals(mode, GitHubGitDataRequests.ExecutableFileMode, StringComparison.Ordinal);
}
