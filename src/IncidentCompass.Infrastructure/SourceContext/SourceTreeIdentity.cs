using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The content identity of a materialized source tree: a lower-hex SHA-256 over the admitted files.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is hashed, in what order.</b> A domain separator, then the admitted file count, then, for
/// every admitted file in ascending <see cref="StringComparer.Ordinal"/> order of its repository
/// path, that path as UTF-8 followed by the lower-hex SHA-256 of the file's exact bytes. Every field
/// is length-prefixed with a four-byte big-endian count, the framing the action-approval contract
/// already uses, so no rearrangement of path and hash text produces the same digest.
/// </para>
/// <para>
/// <b>Normalization, and what is deliberately not normalized.</b> The sort key is the repository
/// path with <c>/</c> separators, so the digest does not inherit the enumeration order of the
/// filesystem, which differs between NTFS and ext4, nor any culture-sensitive collation. Case is
/// preserved exactly as the filesystem reported it, because a case-only rename is a real change on a
/// case-sensitive filesystem and folding would hide it. File bytes are hashed raw: no line-ending
/// translation, no encoding sniff, no trailing-newline repair. That is the load-bearing decision.
/// A diff applies to bytes, so a CRLF checkout and an LF checkout of the same upstream commit are
/// two different bases and must carry two different identities; normalizing would let this type
/// claim "same base" for two trees where the same patch produces different results.
/// </para>
/// <para>
/// <b>What an identity proves.</b> Two equal identities mean the same set of repository paths with
/// the same bytes under each, up to SHA-256 collision resistance, computed under the same admission
/// rules. An identity recorded with a diff therefore names the exact base that diff applies to, and
/// recomputing it later says whether that base still exists.
/// </para>
/// <para>
/// <b>What it does not prove.</b> It is not a commit id and cannot be exchanged for one: nothing
/// here reads git, so an identity says nothing about which commit, branch or upstream repository the
/// tree came from, or whether any commit with these contents exists. It covers no file mode,
/// ownership or timestamp, and no empty directory. It covers only admitted files, so anything the
/// admission rules skip, the monitored root's own <c>.git</c> directory included, is invisible to
/// it. It is a statement about one instant: a tree that changes after materialization still matches
/// the identity of the copy, which is the point of copying, not a claim about the checkout now.
/// Changing the admission rules changes what the digest covers, so
/// <see cref="Domain"/> carries a version and must be bumped when they change, which deliberately
/// makes identities from different rule sets incomparable rather than misleadingly equal.
/// </para>
/// </remarks>
internal static class SourceTreeIdentity
{
    /// <summary>
    /// Domain separator and rule version. Bump the version whenever the admission rules change.
    /// </summary>
    public const string Domain = "IncidentCompass.SourceTreeIdentity.v1";

    public static string Compute(IReadOnlyCollection<SourceTreeEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Encoding.UTF8.GetBytes(Domain));
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(count, entries.Count);
        Append(hash, count);
        foreach (var entry in entries.OrderBy(static entry => entry.RepositoryPath, StringComparer.Ordinal))
        {
            Append(hash, Encoding.UTF8.GetBytes(entry.RepositoryPath));
            Append(hash, Encoding.UTF8.GetBytes(entry.ContentSha256));
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }
}
