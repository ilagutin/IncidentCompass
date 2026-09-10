using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Link and nested-repository refusal asserted as a decision rather than as a filesystem effect.
/// </summary>
/// <remarks>
/// The repository's only other symlink proof cannot create a symlink on a Windows machine without
/// Developer Mode and skips itself there, so the guarantee it is supposed to give evaporates on the
/// machine most likely to need it. These assertions run on every platform and cannot skip: the
/// decision is a pure function of attributes, name and depth, and
/// <c>SourceWorkspaceMaterializerTests</c> proves separately, on a real filesystem and without a
/// link, that the walk consults exactly this function for every entry it enumerates.
/// </remarks>
public sealed class SourceWorkspaceEntryPolicyTests
{
    [Theory]
    [InlineData(FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.Normal | FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)]
    public void Reject_RefusesEveryReparsePointEntry(FileAttributes attributes)
    {
        // Depth 1 is where the monitored root's own `.git` is skipped rather than refused, so a
        // reparse point there proves the link check runs before any admission shortcut.
        Assert.Equal("source_workspace_link_rejected", SourceWorkspaceEntryPolicy.Reject(attributes, "linked", 1));
        Assert.Equal("source_workspace_link_rejected", SourceWorkspaceEntryPolicy.Reject(attributes, ".git", 1));
        Assert.Equal("source_workspace_link_rejected", SourceWorkspaceEntryPolicy.Reject(attributes, "Checkout.cs", 4));
    }

    [Theory]
    [InlineData(".gitmodules", 1)]
    [InlineData(".gitmodules", 5)]
    [InlineData(".GITMODULES", 1)]
    [InlineData(".git", 2)]
    [InlineData(".git", 7)]
    [InlineData(".Git", 2)]
    public void Reject_RefusesSubmoduleAndNestedRepositoryMarkers(string name, int depth)
    {
        Assert.Equal(
            "source_workspace_submodule_rejected",
            SourceWorkspaceEntryPolicy.Reject(FileAttributes.Directory, name, depth));
    }

    [Fact]
    public void Reject_AdmitsOrdinarySourceEntries()
    {
        Assert.Null(SourceWorkspaceEntryPolicy.Reject(FileAttributes.Normal, "Checkout.cs", 1));
        Assert.Null(SourceWorkspaceEntryPolicy.Reject(FileAttributes.Directory, "src", 3));
        Assert.Null(SourceWorkspaceEntryPolicy.Reject(FileAttributes.Normal, ".gitignore", 1));
    }

    [Fact]
    public void IsExcluded_SkipsOnlyTheMonitoredRootsOwnGitDirectory()
    {
        Assert.True(SourceWorkspaceEntryPolicy.IsExcluded(".git", 1));
        Assert.False(SourceWorkspaceEntryPolicy.IsExcluded(".git", 2));
        Assert.False(SourceWorkspaceEntryPolicy.IsExcluded(".gitignore", 1));
        Assert.False(SourceWorkspaceEntryPolicy.IsExcluded("src", 1));
    }
}
