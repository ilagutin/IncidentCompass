using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Remediation;
using IncidentCompass.Infrastructure.SourceContext;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The obligation the applier's own remarks hand to its caller: a diff may only be applied to the
/// tree it was prepared against.
/// </summary>
/// <remarks>
/// The hole this closes is narrow and easy to miss. A hunk that consumes no base line quotes no base
/// line, so it matches at its offset in any file: a patch that only creates a file applies cleanly
/// to a checkout nobody has looked at, produces a result identity, and would be recorded as a
/// successful change against a base that no longer exists. Nothing in the diff text can detect that,
/// so every test here uses an insert-only patch deliberately. A refusal produced by a context line
/// failing to match would prove nothing about the base check, because the context check would have
/// caught it anyway.
/// </remarks>
public sealed class RemediationBaseObligationTests : IDisposable
{
    private const string ServiceName = "checkout-api";
    private const string Release = "1.4";

    /// <summary>
    /// A creation, which quotes no base line and therefore applies to any tree that does not already
    /// hold the file. Only the base identity binds it to a particular checkout.
    /// </summary>
    private static readonly string InsertOnlyPatch = string.Join('\n',
        "--- /dev/null",
        "+++ b/src/Discount.cs",
        "@@ -0,0 +1,2 @@",
        "+namespace Shop;",
        "+public class Discount;",
        string.Empty);

    private readonly string temporaryRoot = Directory.CreateTempSubdirectory("ic-base-").FullName;

    private string MonitoredRoot => Path.Combine(temporaryRoot, "monitored");

    private string WorkspaceRoot => Path.Combine(temporaryRoot, "workspaces");

    [Fact]
    public async Task Apply_RefusesAPatchPreparedAgainstADifferentBaseBeforeApplyingIt()
    {
        Write("src/Checkout.cs", "namespace Shop;\npublic class Checkout;\n");
        var workspace = CreateWorkspace();
        var staleBase = await IdentifyAsync(workspace);

        // The monitored checkout moves, exactly as a deployment or a pull would move it.
        Write("src/Basket.cs", "namespace Shop;\npublic class Basket;\n");
        var currentBase = await IdentifyAsync(workspace);
        Assert.NotEqual(staleBase, currentBase);

        var refused = await ApplyAsync(workspace, staleBase, InsertOnlyPatch);

        Assert.Equal(RemediationCodes.BaseMismatch, refused.Code);
        Assert.Null(refused.ResultTreeIdentity);
        Assert.Equal(0, refused.FilesChanged);

        // The same patch against the base that is actually there applies, so the refusal above came
        // from the base check and not from anything wrong with the diff.
        var applied = await ApplyAsync(workspace, currentBase, InsertOnlyPatch);

        Assert.Equal(RemediationCodes.Applied, applied.Code);
        Assert.Equal(1, applied.FilesChanged);
        Assert.NotEqual(currentBase, applied.ResultTreeIdentity);
    }

    /// <summary>
    /// A mismatched base is not a formatting mistake, so it must not be reprompted as one.
    /// </summary>
    [Fact]
    public async Task Apply_DoesNotOfferAMismatchedBaseToTheModelAsSomethingToCorrect()
    {
        Write("src/Checkout.cs", "namespace Shop;\npublic class Checkout;\n");
        var workspace = CreateWorkspace();

        var refused = await ApplyAsync(workspace, new string('0', 64), InsertOnlyPatch);

        Assert.Equal(RemediationCodes.BaseMismatch, refused.Code);
        Assert.False(refused.AnswerCorrectable);
    }

    /// <summary>
    /// A refusal about the diff is the model's to fix, and the adapter says so rather than leaving a
    /// caller to guess from the shape of a code string.
    /// </summary>
    [Fact]
    public async Task Apply_MarksADiffRefusalAsSomethingTheModelCouldCorrect()
    {
        Write("src/Checkout.cs", "namespace Shop;\npublic class Checkout;\n");
        var workspace = CreateWorkspace();
        var identity = await IdentifyAsync(workspace);

        var refused = await ApplyAsync(workspace, identity, "--- a/src/Checkout.cs\nnot a hunk\n");

        Assert.True(refused.AnswerCorrectable);
        Assert.StartsWith("source_patch_", refused.Code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The identity a base is named by is the identity a workspace of the same tree carries, which
    /// is what makes the comparison mean anything at all.
    /// </summary>
    [Fact]
    public async Task IdentifyBase_AgreesWithWhatMaterializingTheSameTreeRecords()
    {
        Write("src/Checkout.cs", "namespace Shop;\npublic class Checkout;\n");
        Write("src/nested/Basket.cs", "namespace Shop;\npublic class Basket;\n");
        var workspace = CreateWorkspace();

        var named = await IdentifyAsync(workspace);

        var materialized = await new SourceWorkspaceMaterializer(
                Path.Combine(temporaryRoot, "control"),
                SourceWorkspaceBounds.Default)
            .MaterializeAsync(MonitoredRoot, TestContext.Current.CancellationToken);
        using var copy = materialized.Workspace;
        Assert.NotNull(copy);
        Assert.Equal(copy.TreeIdentity, named);
    }

    [Fact]
    public async Task Apply_RefusesAnUnconfiguredTarget()
    {
        Write("src/Checkout.cs", "namespace Shop;\npublic class Checkout;\n");
        var workspace = CreateWorkspace(workspaceRoot: null);

        var identified = await workspace.IdentifyBaseAsync(
            new RemediationTarget(ServiceName, Release),
            TestContext.Current.CancellationToken);
        var applied = await ApplyAsync(workspace, new string('0', 64), InsertOnlyPatch);

        Assert.Equal(RemediationCodes.NotConfigured, identified.Code);
        Assert.Null(identified.TreeIdentity);
        Assert.Equal(RemediationCodes.NotConfigured, applied.Code);
        Assert.False(applied.AnswerCorrectable);
        Assert.False(Directory.Exists(WorkspaceRoot));
    }

    [Fact]
    public async Task Apply_RefusesAServiceAndReleaseNoConfiguredRootAnswersFor()
    {
        Write("src/Checkout.cs", "namespace Shop;\npublic class Checkout;\n");
        var workspace = CreateWorkspace();

        var identified = await workspace.IdentifyBaseAsync(
            new RemediationTarget(ServiceName, "9.9"),
            TestContext.Current.CancellationToken);

        Assert.Equal(RemediationCodes.NotConfigured, identified.Code);
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

    private static async Task<string> IdentifyAsync(LocalSourceRemediationWorkspace workspace)
    {
        var result = await workspace.IdentifyBaseAsync(
            new RemediationTarget(ServiceName, Release),
            TestContext.Current.CancellationToken);
        Assert.Equal(RemediationCodes.BaseIdentified, result.Code);
        Assert.NotNull(result.TreeIdentity);
        return result.TreeIdentity;
    }

    private static Task<RemediationApplyResult> ApplyAsync(
        LocalSourceRemediationWorkspace workspace,
        string baseTreeIdentity,
        string patch) =>
        workspace.ApplyAsync(
            new RemediationApplyRequest(new RemediationTarget(ServiceName, Release), baseTreeIdentity, patch),
            TestContext.Current.CancellationToken);

    private LocalSourceRemediationWorkspace CreateWorkspace(string? workspaceRoot = "") =>
        new(Options.Create(new SourceContextOptions
        {
            WorkspaceRoot = workspaceRoot is "" ? WorkspaceRoot : workspaceRoot,
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
