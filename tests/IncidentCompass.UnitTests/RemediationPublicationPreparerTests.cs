using System.Text;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.SourceContext;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// What a push would carry, derived against a real filesystem: the proof, the exclusions, and the
/// bytes.
/// </summary>
/// <remarks>
/// The workspace is a real monitored checkout copied by the real materializer, so the admitted set
/// these tests compare against a remote listing is the admitted set production would compare. That is
/// the point of testing here rather than against a fake: the gap this feature had to close is exactly
/// the one between "what admission sees" and "what a repository tracks".
/// </remarks>
public sealed class RemediationPublicationPreparerTests : IDisposable
{
    private const string BaseContent = "namespace A;\n";
    private const string PatchText =
        "--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-namespace A;\n+namespace B;\n";

    private readonly string temporaryRoot = Directory.CreateTempSubdirectory("ic-publication-").FullName;

    private string MonitoredRoot => Path.Combine(temporaryRoot, "monitored");

    private string WorkspaceRoot => Path.Combine(temporaryRoot, "workspaces");

    public void Dispose()
    {
        try
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public async Task AProvedBaseYieldsOnlyTheFilesTheDiffWrote()
    {
        Write("src/A.cs", BaseContent);
        Write("src/untouched.cs", "namespace Untouched;\n");

        var result = await PrepareAsync(Remote(
            ("src/A.cs", BaseContent),
            ("src/untouched.cs", "namespace Untouched;\n")));

        Assert.Equal(RemediationPublicationCodes.Prepared, result.Code);
        Assert.Equal(2, result.ProvedPathCount);
        Assert.Empty(result.LocalOnlyPaths);
        var file = Assert.Single(result.ChangedFiles);
        Assert.Equal("src/A.cs", file.RepositoryPath);
        Assert.Equal("namespace B;\n", Encoding.UTF8.GetString(file.Content!));
    }

    /// <summary>
    /// The untracked surplus a working checkout carries - build output, a local environment file - is
    /// named in the result and is not among the files a push would carry.
    /// </summary>
    [Fact]
    public async Task LocalOnlyPathsAreListedAndNeverAmongTheFilesAPushWouldCarry()
    {
        Write("src/A.cs", BaseContent);
        Write("obj/Debug/build.log", "log\n");
        Write(".env", "TOKEN=value\n");

        var result = await PrepareAsync(Remote(("src/A.cs", BaseContent)));

        Assert.Equal(RemediationPublicationCodes.Prepared, result.Code);
        Assert.Equal(1, result.ProvedPathCount);
        Assert.Equal([".env", "obj/Debug/build.log"], result.LocalOnlyPaths);
        Assert.Equal(["src/A.cs"], result.ChangedFiles.Select(file => file.RepositoryPath));
    }

    [Fact]
    public async Task ABaseThatCannotBeProvedRefusesAndYieldsNothing()
    {
        Write("src/A.cs", BaseContent);

        var diverged = await PrepareAsync(Remote(("src/A.cs", "namespace Other;\n")));
        var missingLocally = await PrepareAsync(Remote(
            ("src/A.cs", BaseContent),
            ("src/Deleted.cs", "namespace Deleted;\n")));

        Assert.Equal(GitTreeCorrespondenceCodes.ContentDiverged, diverged.Code);
        Assert.Equal(GitTreeCorrespondenceCodes.PathMissingLocally, missingLocally.Code);
        Assert.All(new[] { diverged, missingLocally }, result =>
        {
            Assert.Null(result.CorrespondenceDigest);
            Assert.Empty(result.ChangedFiles);
        });
    }

    /// <summary>
    /// The base is checked before the correspondence and before the patch, so a checkout that moved
    /// is reported as a moved checkout rather than as a diverged remote.
    /// </summary>
    [Fact]
    public async Task AMovedCheckoutRefusesBeforeAnythingElseIsLookedAt()
    {
        Write("src/A.cs", BaseContent);

        var result = await PrepareAsync(
            Remote(("src/A.cs", BaseContent)),
            baseTreeIdentity: new string('9', 64));

        Assert.Equal(RemediationCodes.BaseMismatch, result.Code);
    }

    [Fact]
    public async Task ADiffProducingAnUnapprovedTreeRefuses()
    {
        Write("src/A.cs", BaseContent);

        var result = await PrepareAsync(
            Remote(("src/A.cs", BaseContent)),
            resultTreeIdentity: new string('8', 64));

        Assert.Equal(RemediationPublicationCodes.ResultMismatch, result.Code);
    }

    [Fact]
    public async Task TheMonitoredCheckoutIsNeverChanged()
    {
        Write("src/A.cs", BaseContent);

        await PrepareAsync(Remote(("src/A.cs", BaseContent)));

        Assert.Equal(
            BaseContent,
            await File.ReadAllTextAsync(
                Path.Combine(MonitoredRoot, "src", "A.cs"), TestContext.Current.CancellationToken));
    }

    private async Task<RemediationPublicationResult> PrepareAsync(
        Dictionary<string, string> remote,
        string? baseTreeIdentity = null,
        string? resultTreeIdentity = null)
    {
        var workspace = CreateWorkspace();
        var identity = await MeasureAsync();
        var applied = await MeasureAppliedAsync();
        var request = new RemediationPublicationRequest(
            new RemediationTarget("checkout", "2026.1"),
            baseTreeIdentity ?? identity,
            resultTreeIdentity ?? applied,
            PatchText,
            new string('a', 40),
            new string('b', 40),
            remote);
        return await workspace.PrepareForPublicationAsync(request, TestContext.Current.CancellationToken);
    }

    private LocalSourceRemediationWorkspace CreateWorkspace() => new(Options.Create(new SourceContextOptions
    {
        WorkspaceRoot = WorkspaceRoot,
        Roots = [new SourceContextRootOptions { ServiceName = "checkout", Release = "2026.1", RootPath = MonitoredRoot }]
    }));

    private async Task<string> MeasureAsync()
    {
        var result = await CreateWorkspace().IdentifyBaseAsync(
            new RemediationTarget("checkout", "2026.1"), TestContext.Current.CancellationToken);
        return result.TreeIdentity!;
    }

    private async Task<string> MeasureAppliedAsync()
    {
        var result = await CreateWorkspace().ApplyAsync(
            new RemediationApplyRequest(
                new RemediationTarget("checkout", "2026.1"), await MeasureAsync(), PatchText),
            TestContext.Current.CancellationToken);
        return result.ResultTreeIdentity!;
    }

    /// <summary>
    /// A remote listing built the way a provider would report one: path to git blob id.
    /// </summary>
    private static Dictionary<string, string> Remote(params (string Path, string Content)[] entries) =>
        entries.ToDictionary(
            entry => entry.Path,
            entry => GitBlobIdentity.Compute(Encoding.UTF8.GetBytes(entry.Content)),
            StringComparer.Ordinal);

    private void Write(string relativePath, string content)
    {
        var absolute = Path.Combine(MonitoredRoot, Path.Combine(relativePath.Split('/')));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }
}
