using System.Text.Json;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Reads the provider's answers about pull requests, and refuses anything that is not exactly what was
/// asked about.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only one value leaves: a number.</b> A pull-request object carries a title, a body, an author, a
/// URL, labels and a merge state, all of them written by whoever opened it or by anyone who edited it
/// since. None of them is read. What comes back is the provider's own integer, which is the only field
/// this product records and the only one the audit projection can hold.
/// </para>
/// <para>
/// <b>Matching is exact and three-sided.</b> A pull request answers for this head only when its head
/// reference is the derived branch, its head commit is the commit the approval named, and its base is
/// the configured base branch. The query already filters on two of those; checking all three here means
/// a provider that ignored a filter, or a pull request whose head was force-updated by someone else,
/// produces a refusal rather than a number this product would then treat as its own.
/// </para>
/// <para>
/// <b>A null head commit is the reconciliation's question, not a weaker check.</b> Settling an unknown
/// outcome asks whether a pull request exists for a branch at all, and the caller has no commit to
/// offer; the head reference and the base are still matched exactly. Every other caller passes the
/// commit the approval froze.
/// </para>
/// </remarks>
internal static class GitHubPullRequestParser
{
    /// <summary>Largest pull-request number this adapter will accept from a provider.</summary>
    private const int MaximumNumber = 1_000_000_000;

    /// <summary>
    /// Reads a listing of at most two pull requests into one number or one closed code.
    /// </summary>
    public static (int? Number, string Code) ReadListing(
        JsonElement root,
        string headBranch,
        string? headCommitSha,
        string baseBranch)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 2)
        {
            return (null, CodePublicationCodes.ResponseMalformed);
        }

        if (root.GetArrayLength() == 0)
        {
            return (null, CodePublicationCodes.PullRequestAbsent);
        }

        if (root.GetArrayLength() > 1)
        {
            return (null, CodePublicationCodes.PullRequestAmbiguous);
        }

        return ReadOne(root[0], headBranch, headCommitSha, baseBranch);
    }

    /// <summary>
    /// Reads one pull-request object into its number, or refuses. Used for the create response, whose
    /// body is a single object rather than a listing.
    /// </summary>
    public static (int? Number, string Code) ReadOne(
        JsonElement root,
        string headBranch,
        string? headCommitSha,
        string baseBranch)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("number", out var number) ||
            !number.TryGetInt32(out var value) || value is < 1 or > MaximumNumber)
        {
            return (null, CodePublicationCodes.ResponseMalformed);
        }

        return HasReference(root, "head", headBranch, headCommitSha) &&
            HasReference(root, "base", baseBranch, expectedSha: null)
            ? (value, CodePublicationCodes.PullRequestAlreadyOpen)
            : (null, CodePublicationCodes.PullRequestMismatch);
    }

    private static bool HasReference(
        JsonElement root,
        string name,
        string expectedRef,
        string? expectedSha) =>
        root.TryGetProperty(name, out var reference) &&
        reference.ValueKind == JsonValueKind.Object &&
        reference.TryGetProperty("ref", out var referenceName) &&
        referenceName.ValueKind == JsonValueKind.String &&
        string.Equals(referenceName.GetString(), expectedRef, StringComparison.Ordinal) &&
        (expectedSha is null || HasCommit(reference, expectedSha));

    private static bool HasCommit(JsonElement reference, string expectedSha) =>
        reference.TryGetProperty("sha", out var sha) && sha.ValueKind == JsonValueKind.String &&
        GitBlobIdentity.IsValid(sha.GetString()) &&
        string.Equals(sha.GetString(), expectedSha, StringComparison.Ordinal);
}
