using System.Security.Cryptography;
using System.Text;
using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Materialization against a real filesystem: what it copies, what it refuses, that the monitored
/// checkout is never touched, and that no terminal path leaves a workspace behind.
/// </summary>
public sealed class SourceWorkspaceMaterializerTests : IDisposable
{
    private readonly string temporaryRoot = Directory.CreateTempSubdirectory("ic-workspace-").FullName;

    private string MonitoredRoot => Path.Combine(temporaryRoot, "monitored");

    private string WorkspaceRoot => Path.Combine(temporaryRoot, "workspaces");

    [Fact]
    public async Task Materialize_CopiesTheTreeAndSkipsTheMonitoredRootsOwnGitDirectory()
    {
        WriteSource("src/Checkout.cs", "class Checkout;\n");
        WriteSource("src/nested/Basket.cs", "class Basket;\n");
        WriteSource(".git/HEAD", "ref: refs/heads/main\n");
        WriteSource(".gitignore", "bin/\n");

        var result = await MaterializeAsync();

        using var workspace = AssertCreated(result);
        Assert.Equal(3, workspace.FileCount);
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, "src", "nested", "Basket.cs")));
        Assert.True(File.Exists(Path.Combine(workspace.DirectoryPath, ".gitignore")));
        Assert.False(Directory.Exists(Path.Combine(workspace.DirectoryPath, ".git")));
        Assert.Equal(
            "class Basket;\n",
            await File.ReadAllTextAsync(
                Path.Combine(workspace.DirectoryPath, "src", "nested", "Basket.cs"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Materialize_ProducesTheSameIdentityForTheSameTreeTwice()
    {
        WriteSource("src/Checkout.cs", "class Checkout;\n");
        WriteSource("src/nested/Basket.cs", "class Basket;\n");

        using var first = AssertCreated(await MaterializeAsync());
        using var second = AssertCreated(await MaterializeAsync());

        Assert.Equal(first.TreeIdentity, second.TreeIdentity);
        Assert.NotEqual(first.DirectoryPath, second.DirectoryPath);
    }

    [Fact]
    public async Task Materialize_ProducesADifferentIdentityForAChangedTree()
    {
        WriteSource("src/Checkout.cs", "class Checkout;\n");
        var baseline = AssertCreated(await MaterializeAsync());
        var baseIdentity = baseline.TreeIdentity;
        baseline.Dispose();

        WriteSource("src/Checkout.cs", "class Checkout { }\n");
        using var edited = AssertCreated(await MaterializeAsync());
        Assert.NotEqual(baseIdentity, edited.TreeIdentity);

        WriteSource("src/Checkout.cs", "class Checkout;\n");
        WriteSource("src/Added.cs", "class Added;\n");
        using var added = AssertCreated(await MaterializeAsync());
        Assert.NotEqual(baseIdentity, added.TreeIdentity);
    }

    [Fact]
    public async Task Materialize_TreatsLineEndingsAsPartOfTheBase()
    {
        // A patch applies to bytes. Two checkouts of the same upstream commit under different
        // line-ending settings are two different bases, and must not share one identity.
        WriteSource("src/Checkout.cs", "class Checkout;\nclass Basket;\n");
        var unixIdentity = AssertCreated(await MaterializeAsync());
        var unix = unixIdentity.TreeIdentity;
        unixIdentity.Dispose();

        WriteSource("src/Checkout.cs", "class Checkout;\r\nclass Basket;\r\n");
        using var windows = AssertCreated(await MaterializeAsync());

        Assert.NotEqual(unix, windows.TreeIdentity);
    }

    [Fact]
    public async Task Materialize_RefusesANestedRepositoryWithoutNeedingALink()
    {
        // The walk consults SourceWorkspaceEntryPolicy once per entry, for links and nested
        // repositories alike. This proves the call site on a real filesystem; the link decision
        // itself is asserted in SourceWorkspaceEntryPolicyTests, which cannot skip.
        WriteSource("src/Checkout.cs", "class Checkout;\n");
        WriteSource("vendor/library/.git/HEAD", "ref: refs/heads/main\n");

        await AssertRefusedAsync("source_workspace_submodule_rejected");
    }

    [Fact]
    public async Task Materialize_RefusesADeclaredSubmodule()
    {
        WriteSource("src/Checkout.cs", "class Checkout;\n");
        WriteSource(".gitmodules", "[submodule \"library\"]\n");

        await AssertRefusedAsync("source_workspace_submodule_rejected");
    }

    [Fact]
    public async Task Materialize_RefusesASymbolicLinkEscape()
    {
        WriteSource("src/Checkout.cs", "class Checkout;\n");
        var outside = Path.Combine(temporaryRoot, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(
            Path.Combine(outside, "Secret.cs"),
            "secret",
            TestContext.Current.CancellationToken);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(MonitoredRoot, "linked"), outside);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Symbolic links are not available to this test process.");
        }

        await AssertRefusedAsync("source_workspace_link_rejected");
    }

    [Fact]
    public async Task Materialize_RefusesAWorkspaceRootInsideTheMonitoredRoot()
    {
        WriteSource("src/Checkout.cs", "class Checkout;\n");
        var materializer = new SourceWorkspaceMaterializer(
            Path.Combine(MonitoredRoot, "scratch"),
            SourceWorkspaceBounds.Default);

        var result = await materializer.MaterializeAsync(MonitoredRoot, TestContext.Current.CancellationToken);

        Assert.Equal("source_workspace_root_rejected", result.Code);
        Assert.Null(result.Workspace);
        Assert.False(Directory.Exists(Path.Combine(MonitoredRoot, "scratch")));
    }

    [Fact]
    public async Task Materialize_RefusesAMissingMonitoredRoot()
    {
        var materializer = new SourceWorkspaceMaterializer(WorkspaceRoot, SourceWorkspaceBounds.Default);

        var result = await materializer.MaterializeAsync(
            Path.Combine(temporaryRoot, "absent"),
            TestContext.Current.CancellationToken);

        Assert.Equal("source_root_unavailable", result.Code);
        Assert.Null(result.Workspace);
    }

    [Fact]
    public async Task Materialize_RefusesATreeOverTheFileBound()
    {
        WriteSource("a.cs", "one");
        WriteSource("b.cs", "two");
        WriteSource("c.cs", "three");

        await AssertRefusedAsync(
            "source_workspace_file_limit",
            SourceWorkspaceBounds.Default with { MaxFiles = 2 });
    }

    [Fact]
    public async Task Materialize_RefusesATreeOverTheByteBound()
    {
        WriteSource("a.cs", new string('x', 64));
        WriteSource("b.cs", new string('y', 64));

        await AssertRefusedAsync(
            "source_workspace_size_limit",
            SourceWorkspaceBounds.Default with { MaxTotalBytes = 100 });
    }

    [Fact]
    public async Task Materialize_RefusesATreeOverTheDepthBound()
    {
        WriteSource("one/two/three/Deep.cs", "class Deep;\n");

        await AssertRefusedAsync(
            "source_workspace_depth_limit",
            SourceWorkspaceBounds.Default with { MaxDepth = 2 });
    }

    [Fact]
    public async Task Materialize_LeavesNoWorkspaceBehindOnRefusal()
    {
        WriteSource("a.cs", "one");
        WriteSource("b.cs", "two");
        var before = FingerprintMonitoredRoot();

        await AssertRefusedAsync(
            "source_workspace_file_limit",
            SourceWorkspaceBounds.Default with { MaxFiles = 1 });

        Assert.Empty(Directory.GetFileSystemEntries(WorkspaceRoot));
        Assert.Equal(before, FingerprintMonitoredRoot());
    }

    [Fact]
    public async Task Materialize_LeavesNoWorkspaceBehindOnCancellation()
    {
        WriteSource("src/Checkout.cs", "class Checkout;\n");
        var before = FingerprintMonitoredRoot();
        var materializer = new SourceWorkspaceMaterializer(WorkspaceRoot, SourceWorkspaceBounds.Default);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => materializer.MaterializeAsync(MonitoredRoot, cancellation.Token));

        Assert.True(Directory.Exists(WorkspaceRoot));
        Assert.Empty(Directory.GetFileSystemEntries(WorkspaceRoot));
        Assert.Equal(before, FingerprintMonitoredRoot());
    }

    [Fact]
    public async Task Materialize_DisposalRemovesTheWorkspaceAndLeavesTheCheckoutUntouched()
    {
        WriteSource("src/Checkout.cs", "class Checkout;\n");
        WriteSource(".git/HEAD", "ref: refs/heads/main\n");
        var before = FingerprintMonitoredRoot();

        var workspace = AssertCreated(await MaterializeAsync());
        var workspacePath = workspace.DirectoryPath;
        Assert.True(Directory.Exists(workspacePath));
        Assert.Equal(before, FingerprintMonitoredRoot());

        workspace.Dispose();

        Assert.False(Directory.Exists(workspacePath));
        Assert.Empty(Directory.GetFileSystemEntries(WorkspaceRoot));
        Assert.Equal(before, FingerprintMonitoredRoot());
        workspace.Dispose();
    }

    public void Dispose()
    {
        Directory.Delete(temporaryRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Task<SourceWorkspaceResult> MaterializeAsync(SourceWorkspaceBounds? bounds = null) =>
        new SourceWorkspaceMaterializer(WorkspaceRoot, bounds ?? SourceWorkspaceBounds.Default)
            .MaterializeAsync(MonitoredRoot, TestContext.Current.CancellationToken);

    private static SourceWorkspace AssertCreated(SourceWorkspaceResult result)
    {
        Assert.Equal("source_workspace_created", result.Code);
        Assert.NotNull(result.Workspace);
        return result.Workspace;
    }

    private async Task AssertRefusedAsync(string expectedCode, SourceWorkspaceBounds? bounds = null)
    {
        var before = FingerprintMonitoredRoot();

        var result = await MaterializeAsync(bounds);

        Assert.Equal(expectedCode, result.Code);
        Assert.Null(result.Workspace);
        Assert.False(result.IsCreated);
        Assert.Empty(Directory.GetFileSystemEntries(WorkspaceRoot));
        Assert.Equal(before, FingerprintMonitoredRoot());
    }

    private void WriteSource(string relativePath, string content)
    {
        var path = Path.Combine(MonitoredRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// An independent description of the monitored checkout: every directory and every file below it
    /// with its exact bytes. It is built here rather than through <c>SourceTreeIdentity</c> so a
    /// change that broke both the copier and the identity together could not hide in it, and it
    /// covers the entries the copier deliberately skips, so a deleted <c>.git</c> would be visible.
    /// </summary>
    private string FingerprintMonitoredRoot()
    {
        var lines = Directory
            .EnumerateFileSystemEntries(MonitoredRoot, "*", SearchOption.AllDirectories)
            .Select(entry => Directory.Exists(entry)
                ? "dir " + Path.GetRelativePath(MonitoredRoot, entry).Replace('\\', '/')
                : "file " + Path.GetRelativePath(MonitoredRoot, entry).Replace('\\', '/') + " " +
                  Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entry))))
            .Order(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }
}
