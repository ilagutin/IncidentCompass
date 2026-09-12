using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// Proves, path by path, that an approved local base tree and one remote commit's tree hold the same
/// bytes wherever both describe a path, and says exactly what was left out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a proof is needed at all.</b> A push builds its commit on the remote base commit's tree and
/// overlays only the files the approved diff writes. That is the only shape that does not publish a
/// working directory, but on its own it assumes the remote base and the approved base are the same
/// tree, and nothing in this product connected them: a tree identity is a SHA-256 over admitted files
/// and says nothing about which commit a checkout is at. This comparison is that connection, and it
/// is made from bytes rather than from a revision label.
/// </para>
/// <para>
/// <b>The three classes, and what each one means.</b> A path both sides hold with equal git blob ids
/// is proved. A path both sides hold with different ids means the checkout moved or is dirty, and the
/// push is refused. A path only the local base holds is untracked or ignored by the remote
/// repository: it is enumerated, excluded from the push, and covered by the digest, because the honest
/// claim is "byte-equivalent over the proved intersection, with these paths excluded" rather than
/// "byte-equivalent".
/// </para>
/// <para>
/// <b>A path only the remote tree holds refuses.</b> It is one of exactly two things, and both are
/// divergence. Either the checkout deleted the file, in which case the pushed commit would silently
/// keep it because the base tree does; or the admission rules could not see it, in which case the
/// pushed tree carries content the approved base never described and the claim of equivalence would be
/// quietly excluding an unbounded set nobody enumerated. Accepting the intersection as whatever the
/// local filesystem happened to expose would make the proof a function of what was hidden from it.
/// A checkout of the base branch has no such path, so the strict answer costs nothing in the normal
/// case and is the only one that keeps the proof total over the remote side.
/// </para>
/// <para>
/// <b>What it still does not prove.</b> Nothing about file modes, ownership, empty directories or
/// history, and nothing about paths the remote repository does not track. It is a statement about the
/// bytes under the paths both sides named, at one instant, and the digest says which paths those were.
/// </para>
/// </remarks>
internal static class GitTreeCorrespondence
{
    /// <summary>Domain separator and rule version. Bump it whenever the classification changes.</summary>
    public const string Domain = "IncidentCompass.GitTreeCorrespondence.v1";

    public static GitTreeCorrespondenceOutcome Compare(
        string baseTreeIdentity,
        string remoteCommitSha,
        string remoteTreeSha,
        IReadOnlyDictionary<string, string> localBlobIds,
        IReadOnlyDictionary<string, string> remoteBlobIds)
    {
        if (remoteBlobIds.Count == 0)
        {
            return GitTreeCorrespondenceOutcome.Refused(GitTreeCorrespondenceCodes.RemoteTreeEmpty);
        }

        var proved = 0;
        foreach (var remote in remoteBlobIds)
        {
            if (!localBlobIds.TryGetValue(remote.Key, out var local))
            {
                return GitTreeCorrespondenceOutcome.Refused(
                    GitTreeCorrespondenceCodes.PathMissingLocally);
            }

            if (!string.Equals(local, remote.Value, StringComparison.Ordinal))
            {
                return GitTreeCorrespondenceOutcome.Refused(
                    GitTreeCorrespondenceCodes.ContentDiverged);
            }

            proved++;
        }

        var localOnly = localBlobIds.Keys
            .Where(path => !remoteBlobIds.ContainsKey(path))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new GitTreeCorrespondenceOutcome(
            GitTreeCorrespondenceCodes.Proved,
            proved,
            localOnly,
            ComputeDigest(baseTreeIdentity, remoteCommitSha, remoteTreeSha, proved, localOnly));
    }

    /// <summary>
    /// The digest an approval freezes. Every field is length-prefixed with a four-byte big-endian
    /// count, the framing the approval contract already uses, so no rearrangement of two fields
    /// produces the same value.
    /// </summary>
    private static string ComputeDigest(
        string baseTreeIdentity,
        string remoteCommitSha,
        string remoteTreeSha,
        int provedPathCount,
        string[] localOnlyPaths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Domain);
        Append(hash, baseTreeIdentity);
        Append(hash, remoteCommitSha);
        Append(hash, remoteTreeSha);
        Append(hash, provedPathCount.ToString(CultureInfo.InvariantCulture));
        Append(hash, localOnlyPaths.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var path in localOnlyPaths)
        {
            Append(hash, path);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
