namespace IncidentCompass.Application.Remediation;

/// <summary>
/// One attempt to derive, from an approved base and an approved diff, the exact files a push would
/// carry, and to prove that the approved base is the remote commit the push would build on.
/// </summary>
/// <remarks>
/// The remote side is an input rather than something the workspace fetches. The workspace is a local
/// filesystem adapter and has no remote, no credential and no endpoint; a caller that already read a
/// remote tree hands over the two names and the listing, and gets back a classification. Keeping it
/// that way is what lets the comparison be a pure function of two dictionaries and be tested as one.
/// </remarks>
/// <param name="Target">Which monitored checkout to copy. Backend-selected, as always.</param>
/// <param name="BaseTreeIdentity">
/// The tree the approved diff was prepared against. The copy is refused unless it is this tree, and
/// the refusal happens before the diff is parsed.
/// </param>
/// <param name="ResultTreeIdentity">
/// The tree the approval says the diff produces. The copy is patched and re-identified, and a
/// difference refuses, so the bytes a push would carry are proved to be the approved ones.
/// </param>
/// <param name="PatchText">The exact approved unified diff.</param>
/// <param name="RemoteCommitSha">The remote commit a push would name as its parent.</param>
/// <param name="RemoteTreeSha">That commit's tree.</param>
/// <param name="RemoteBlobIds">
/// Repository path to git blob id for every regular file the remote commit's tree holds. Already
/// filtered by the caller to entries this product can compare at all.
/// </param>
public sealed record RemediationPublicationRequest(
    RemediationTarget Target,
    string BaseTreeIdentity,
    string ResultTreeIdentity,
    string PatchText,
    string RemoteCommitSha,
    string RemoteTreeSha,
    IReadOnlyDictionary<string, string> RemoteBlobIds);
