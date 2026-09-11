using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.SourceContext;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Where the remediation workspace adapter stops calling something a filesystem problem.
/// </summary>
/// <remarks>
/// <c>SourcePatchApplier</c> argues at length that <see cref="ArgumentException" /> and
/// <see cref="NotSupportedException" /> are answers about arguments this code chose, so catching them
/// would turn a logic defect into <c>source_workspace_unavailable</c>, a code whose documentation
/// says a filesystem error ended the attempt. That argument held only as far as the applier's own
/// file: the adapter one layer up caught exactly those two around the whole apply call, so every
/// defect inside the diff engine arrived as a disk problem that had not happened - and, because that
/// code is not answer-correctable, the pass then decided the model had nothing to fix and did not
/// reprompt. These tests hold the narrowed line from both sides.
/// </remarks>
public sealed class RemediationDefectBoundaryTests : IDisposable
{
    private const string ServiceName = "checkout-api";
    private const string Release = "1.4";

    private static readonly string InsertOnlyPatch = string.Join('\n',
        "--- /dev/null",
        "+++ b/src/Discount.cs",
        "@@ -0,0 +1,2 @@",
        "+namespace Shop;",
        "+public class Discount;",
        string.Empty);

    private readonly string temporaryRoot = Directory.CreateTempSubdirectory("ic-defect-").FullName;

    private string MonitoredRoot => Path.Combine(temporaryRoot, "monitored");

    private string WorkspaceRoot => Path.Combine(temporaryRoot, "workspaces");

    /// <summary>
    /// A defect reached from inside the diff engine surfaces as a defect.
    /// </summary>
    /// <remarks>
    /// The defect used here is a real reachable one rather than an injected fault: the extension
    /// allowlist the patch path policy consults comes straight from host options, and a null one
    /// makes the allowlist check throw <see cref="ArgumentNullException" /> - an
    /// <see cref="ArgumentException" /> - from inside the parser, after the base check has already
    /// passed. Options validation would normally reject that configuration, which is exactly why the
    /// adapter must not quietly absorb it: an adapter that relies on someone else having validated
    /// reports a disk failure for a configuration mistake nobody can see.
    /// </remarks>
    [Fact]
    public async Task ADefectInsideTheDiffEngineIsNotReportedAsAnUnavailableFilesystem()
    {
        Write("src/Checkout.cs", "namespace Shop;\npublic class Checkout;\n");
        var workspace = CreateWorkspace(allowedExtensions: null);
        var identified = await workspace.IdentifyBaseAsync(
            new RemediationTarget(ServiceName, Release), TestContext.Current.CancellationToken);
        Assert.Equal(RemediationCodes.BaseIdentified, identified.Code);
        Assert.NotNull(identified.TreeIdentity);

        await Assert.ThrowsAsync<ArgumentNullException>(() => workspace.ApplyAsync(
            new RemediationApplyRequest(
                new RemediationTarget(ServiceName, Release), identified.TreeIdentity, InsertOnlyPatch),
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The other side of the narrowing: what the two wider catches were actually protecting is
    /// resolving the configured roots, and that is still covered - by the materializer's own guard,
    /// which is where the string work happens. A configured workspace root the platform cannot parse
    /// refuses as an unavailable workspace rather than throwing.
    /// </summary>
    [Fact]
    public async Task AConfiguredWorkspaceRootThePlatformRejectsStillRefusesRatherThanThrowing()
    {
        Write("src/Checkout.cs", "namespace Shop;\npublic class Checkout;\n");
        var workspace = CreateWorkspace(
            allowedExtensions: [".cs"],
            workspaceRoot: Path.Combine(temporaryRoot, "work\0space"));

        var identified = await workspace.IdentifyBaseAsync(
            new RemediationTarget(ServiceName, Release), TestContext.Current.CancellationToken);

        Assert.Equal(SourceWorkspaceCodes.Unavailable, identified.Code);
        Assert.Null(identified.TreeIdentity);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// <paramref name="allowedExtensions" /> is passed explicitly by both callers, including the one
    /// that wants it null, so that neither test depends on a default meaning what it needs.
    /// </summary>
    private LocalSourceRemediationWorkspace CreateWorkspace(
        string[]? allowedExtensions,
        string? workspaceRoot = null) =>
        new(Options.Create(new SourceContextOptions
        {
            WorkspaceRoot = workspaceRoot ?? WorkspaceRoot,
            AllowedExtensions = allowedExtensions!,
            Roots =
            [
                new SourceContextRootOptions
                {
                    ServiceName = ServiceName,
                    Release = Release,
                    RootPath = MonitoredRoot
                }
            ]
        }));

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(MonitoredRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
