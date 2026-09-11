using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The classification a push depends on: what is proved, what is excluded, and what refuses.
/// </summary>
public sealed class GitTreeCorrespondenceTests
{
    private const string BaseIdentity =
        "1111111111111111111111111111111111111111111111111111111111111111";

    private const string CommitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TreeSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BlobA = "1111111111111111111111111111111111111111";
    private const string BlobB = "2222222222222222222222222222222222222222";

    [Fact]
    public void EverySharedPathAgreeingIsProved()
    {
        var outcome = Compare(
            local: new() { ["src/a.cs"] = BlobA, ["src/b.cs"] = BlobB },
            remote: new() { ["src/a.cs"] = BlobA, ["src/b.cs"] = BlobB });

        Assert.Equal(GitTreeCorrespondenceCodes.Proved, outcome.Code);
        Assert.Equal(2, outcome.ProvedPathCount);
        Assert.Empty(outcome.LocalOnlyPaths);
        Assert.NotNull(outcome.Digest);
    }

    /// <summary>
    /// The untracked surplus a working checkout carries is named and dropped, not published. This is
    /// the whole difference between the honest claim and the one that would ship a build directory.
    /// </summary>
    [Fact]
    public void LocalOnlyPathsAreExcludedAndNamed()
    {
        var outcome = Compare(
            local: new()
            {
                ["src/a.cs"] = BlobA,
                ["obj/Debug/a.dll"] = BlobB,
                [".env"] = BlobB
            },
            remote: new() { ["src/a.cs"] = BlobA });

        Assert.Equal(GitTreeCorrespondenceCodes.Proved, outcome.Code);
        Assert.Equal(1, outcome.ProvedPathCount);
        Assert.Equal([".env", "obj/Debug/a.dll"], outcome.LocalOnlyPaths);
    }

    [Fact]
    public void ASharedPathWithDifferentBytesRefuses()
    {
        var outcome = Compare(
            local: new() { ["src/a.cs"] = BlobA },
            remote: new() { ["src/a.cs"] = BlobB });

        Assert.Equal(GitTreeCorrespondenceCodes.ContentDiverged, outcome.Code);
        Assert.Null(outcome.Digest);
        Assert.Empty(outcome.LocalOnlyPaths);
    }

    /// <summary>
    /// A path the remote holds and the local base does not is divergence, not a narrower
    /// intersection: the pushed tree would keep it, so the commit would carry content the approved
    /// base never described.
    /// </summary>
    [Fact]
    public void APathMissingLocallyRefuses()
    {
        var outcome = Compare(
            local: new() { ["src/a.cs"] = BlobA },
            remote: new() { ["src/a.cs"] = BlobA, ["src/deleted.cs"] = BlobB });

        Assert.Equal(GitTreeCorrespondenceCodes.PathMissingLocally, outcome.Code);
        Assert.Null(outcome.Digest);
    }

    [Fact]
    public void AnEmptyRemoteTreeRefuses()
    {
        var outcome = Compare(local: new() { ["src/a.cs"] = BlobA }, remote: []);

        Assert.Equal(GitTreeCorrespondenceCodes.RemoteTreeEmpty, outcome.Code);
    }

    [Fact]
    public void TheDigestNamesExactlyWhatWasProvedAndWhatWasExcluded()
    {
        var baseline = Compare(
            local: new() { ["src/a.cs"] = BlobA, ["obj/a.dll"] = BlobB },
            remote: new() { ["src/a.cs"] = BlobA });
        var sameAgain = Compare(
            local: new() { ["src/a.cs"] = BlobA, ["obj/a.dll"] = BlobB },
            remote: new() { ["src/a.cs"] = BlobA });
        var anotherExcludedPath = Compare(
            local: new() { ["src/a.cs"] = BlobA, ["obj/b.dll"] = BlobB },
            remote: new() { ["src/a.cs"] = BlobA });
        var anotherCommit = GitTreeCorrespondence.Compare(
            BaseIdentity,
            "cccccccccccccccccccccccccccccccccccccccc",
            TreeSha,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["src/a.cs"] = BlobA },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["src/a.cs"] = BlobA });

        Assert.Equal(baseline.Digest, sameAgain.Digest);
        Assert.NotEqual(baseline.Digest, anotherExcludedPath.Digest);
        Assert.NotEqual(baseline.Digest, anotherCommit.Digest);
    }

    private static GitTreeCorrespondenceOutcome Compare(
        Dictionary<string, string> local,
        Dictionary<string, string> remote) =>
        GitTreeCorrespondence.Compare(BaseIdentity, CommitSha, TreeSha, local, remote);
}
