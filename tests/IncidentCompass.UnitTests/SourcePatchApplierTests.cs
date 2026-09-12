using System.Security.Cryptography;
using System.Text;
using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Applying a patch against a real workspace: what it produces, what it refuses once it can see the
/// base, and that a refusal leaves the workspace exactly as it was.
/// </summary>
/// <remarks>
/// The workspace under test is a real materialization rather than a hand-built directory, so these
/// exercise the same admission rules and the same tree identity a worker would get, and the expected
/// result of a patch is checked by materializing the tree the patch claims to produce and comparing
/// identities. That makes the assertion independent of the applier's own bookkeeping: the applier
/// could count, plan and record whatever it liked and still not match a tree it did not produce.
/// </remarks>
public sealed class SourcePatchApplierTests : IDisposable
{
    private const string Checkout = "namespace Shop;\npublic class Checkout;\n";
    private const string Basket = "namespace Shop;\npublic class Basket;\n";
    private const string Legacy = "namespace Shop;\npublic class Legacy;\n";

    private static readonly SourcePatchLimits Limits =
        SourcePatchLimits.For(new SourceContextOptions(), SourceWorkspaceBounds.Default);

    private readonly string temporaryRoot = Directory.CreateTempSubdirectory("ic-patch-").FullName;

    private string MonitoredRoot => Path.Combine(temporaryRoot, "monitored");

    private string ExpectedRoot => Path.Combine(temporaryRoot, "expected");

    private string WorkspaceRoot => Path.Combine(temporaryRoot, "workspaces");

    [Fact]
    public async Task Apply_AppliesAModificationACreationAndADeletionAndProducesTheExpectedTree()
    {
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        Write(MonitoredRoot, "src/Basket.cs", Basket);
        Write(MonitoredRoot, "src/Legacy.cs", Legacy);
        Write(MonitoredRoot, ".gitignore", "bin/\n");

        // The same tree as the patch claims to produce, materialized independently.
        Write(ExpectedRoot, "src/Checkout.cs", "namespace Shop;\npublic sealed class Checkout;\n");
        Write(ExpectedRoot, "src/Basket.cs", Basket);
        Write(ExpectedRoot, "src/Discount.cs", "namespace Shop;\npublic class Discount;\n");
        Write(ExpectedRoot, ".gitignore", "bin/\n");
        using var expected = await MaterializeAsync(ExpectedRoot);

        using var workspace = await MaterializeAsync(MonitoredRoot);
        var baseIdentity = workspace.TreeIdentity;
        var result = await ApplyAsync(workspace, Patch(
            "diff --git a/src/Checkout.cs b/src/Checkout.cs",
            "index 1a2b3c4..5d6e7f8 100644",
            "--- a/src/Checkout.cs",
            "+++ b/src/Checkout.cs",
            "@@ -1,2 +1,2 @@",
            " namespace Shop;",
            "-public class Checkout;",
            "+public sealed class Checkout;",
            "diff --git a/src/Discount.cs b/src/Discount.cs",
            "new file mode 100644",
            "--- /dev/null",
            "+++ b/src/Discount.cs",
            "@@ -0,0 +1,2 @@",
            "+namespace Shop;",
            "+public class Discount;",
            "diff --git a/src/Legacy.cs b/src/Legacy.cs",
            "deleted file mode 100644",
            "--- a/src/Legacy.cs",
            "+++ /dev/null",
            "@@ -1,2 +0,0 @@",
            "-namespace Shop;",
            "-public class Legacy;"));

        Assert.Equal("source_patch_applied", result.Code);
        Assert.Equal(3, result.FilesChanged);
        Assert.NotEqual(baseIdentity, result.TreeIdentity);
        Assert.Equal(expected.TreeIdentity, result.TreeIdentity);
        Assert.False(File.Exists(Path.Combine(workspace.DirectoryPath, "src", "Legacy.cs")));
        Assert.Equal(Basket, ReadWorkspace(workspace, "src/Basket.cs"));
    }

    [Fact]
    public async Task Apply_RecomputesTheIdentityFromTheTreeRatherThanFromItsOwnBookkeeping()
    {
        // A scan of an unchanged workspace must reproduce what materialization recorded. Without
        // that the base identity and the result identity would be computed by two walks that could
        // disagree, and no comparison between them would mean anything.
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        Write(MonitoredRoot, "src/nested/Basket.cs", Basket);
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var scan = await SourceTreeScanner.ScanAsync(
            workspace.DirectoryPath,
            SourceWorkspaceBounds.Default,
            TestContext.Current.CancellationToken);

        Assert.NotNull(scan.Entries);
        Assert.Equal(workspace.TreeIdentity, SourceTreeIdentity.Compute(scan.Entries));
    }

    [Fact]
    public async Task Apply_RefusesAPatchWrittenForTheOtherPlatformsLineEndings()
    {
        // The tree identity treats a carriage return as part of the base, so a patch generated
        // against one checkout must not apply to the other. This is the refusal that makes that
        // claim true rather than decorative.
        Write(MonitoredRoot, "src/Checkout.cs", "namespace Shop;\r\npublic class Checkout;\r\n");
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var unixPatch = await ApplyAsync(workspace, Patch(
            "--- a/src/Checkout.cs",
            "+++ b/src/Checkout.cs",
            "@@ -1,2 +1,2 @@",
            " namespace Shop;",
            "-public class Checkout;",
            "+public sealed class Checkout;"));

        Assert.Equal("source_patch_context_mismatch", unixPatch.Code);
        Assert.Equal("namespace Shop;\r\npublic class Checkout;\r\n", ReadWorkspace(workspace, "src/Checkout.cs"));

        var windowsPatch = await ApplyAsync(workspace, string.Join("\r\n",
        [
            "--- a/src/Checkout.cs",
            "+++ b/src/Checkout.cs",
            "@@ -1,2 +1,2 @@",
            " namespace Shop;",
            "-public class Checkout;",
            "+public sealed class Checkout;",
            string.Empty,
        ]));

        Assert.Equal("source_patch_applied", windowsPatch.Code);
        Assert.Equal(
            "namespace Shop;\r\npublic sealed class Checkout;\r\n",
            ReadWorkspace(workspace, "src/Checkout.cs"));
    }

    [Fact]
    public async Task Apply_RefusesAWindowsPatchAgainstAUnixFile()
    {
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var windowsPatch = await ApplyAsync(workspace, string.Join("\r\n",
        [
            "--- a/src/Checkout.cs",
            "+++ b/src/Checkout.cs",
            "@@ -1,2 +1,2 @@",
            " namespace Shop;",
            "-public class Checkout;",
            "+public sealed class Checkout;",
            string.Empty,
        ]));

        Assert.Equal("source_patch_context_mismatch", windowsPatch.Code);
        Assert.Equal(Checkout, ReadWorkspace(workspace, "src/Checkout.cs"));
    }

    [Fact]
    public async Task Apply_KeepsAFileThatEndsWithoutATerminatorEndingWithoutOne()
    {
        Write(MonitoredRoot, "src/Checkout.cs", "namespace Shop;\npublic class Checkout;");
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var result = await ApplyAsync(workspace, Patch(
            "--- a/src/Checkout.cs",
            "+++ b/src/Checkout.cs",
            "@@ -1,2 +1,2 @@",
            " namespace Shop;",
            "-public class Checkout;",
            @"\ No newline at end of file",
            "+public sealed class Checkout;",
            @"\ No newline at end of file"));

        Assert.Equal("source_patch_applied", result.Code);
        Assert.Equal("namespace Shop;\npublic sealed class Checkout;", ReadWorkspace(workspace, "src/Checkout.cs"));
    }

    [Fact]
    public async Task Apply_RefusesAHunkThatIsSilentAboutTheFilesLastByte()
    {
        // The file ends without a terminator and the patch does not say so. Applying would add one,
        // and the result identity would then record a change the diff never described.
        Write(MonitoredRoot, "src/Checkout.cs", "namespace Shop;\npublic class Checkout;");
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var result = await ApplyAsync(workspace, Patch(
            "--- a/src/Checkout.cs",
            "+++ b/src/Checkout.cs",
            "@@ -1,2 +1,2 @@",
            " namespace Shop;",
            "-public class Checkout;",
            "+public sealed class Checkout;"));

        Assert.Equal("source_patch_context_mismatch", result.Code);
        Assert.Equal("namespace Shop;\npublic class Checkout;", ReadWorkspace(workspace, "src/Checkout.cs"));
    }

    [Fact]
    public async Task Apply_RefusesContextThatDoesNotMatchTheBase()
    {
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);
        var before = Fingerprint(workspace);

        var result = await ApplyAsync(workspace, Patch(
            "--- a/src/Checkout.cs",
            "+++ b/src/Checkout.cs",
            "@@ -1,2 +1,2 @@",
            " namespace Warehouse;",
            "-public class Checkout;",
            "+public sealed class Checkout;"));

        Assert.Equal("source_patch_context_mismatch", result.Code);
        Assert.Null(result.TreeIdentity);
        Assert.Equal(before, Fingerprint(workspace));
    }

    [Fact]
    public async Task Apply_RefusesAModificationOfAFileTheBaseDoesNotHave()
    {
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var result = await ApplyAsync(workspace, Patch(
            "--- a/src/Absent.cs",
            "+++ b/src/Absent.cs",
            "@@ -1,1 +1,1 @@",
            "-one",
            "+two"));

        Assert.Equal("source_patch_target_missing", result.Code);
    }

    [Fact]
    public async Task Apply_RefusesADeleteOfAFileTheBaseDoesNotHave()
    {
        // Treating it as already done would let a patch claim to have removed something it never
        // saw, and the result identity would equal the base identity for a patch that said it
        // changed the tree.
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var result = await ApplyAsync(workspace, Patch(
            "--- a/src/Absent.cs",
            "+++ /dev/null",
            "@@ -1,1 +0,0 @@",
            "-one"));

        Assert.Equal("source_patch_target_missing", result.Code);
    }

    [Fact]
    public async Task Apply_RefusesACreateOfAFileTheBaseAlreadyHas()
    {
        // A create quotes no base line, so overwriting here would be the one way a change reaches a
        // file without passing the context check at all.
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);
        var before = Fingerprint(workspace);

        var result = await ApplyAsync(workspace, Patch(
            "--- /dev/null",
            "+++ b/src/Checkout.cs",
            "@@ -0,0 +1,1 @@",
            "+replaced"));

        Assert.Equal("source_patch_target_exists", result.Code);
        Assert.Equal(before, Fingerprint(workspace));
    }

    [Fact]
    public async Task Apply_RefusesADeleteThatDidNotQuoteTheWholeFile()
    {
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var result = await ApplyAsync(workspace, Patch(
            "--- a/src/Checkout.cs",
            "+++ /dev/null",
            "@@ -1,1 +0,0 @@",
            "-namespace Shop;"));

        Assert.Equal("source_patch_context_mismatch", result.Code);
        Assert.Equal(Checkout, ReadWorkspace(workspace, "src/Checkout.cs"));
    }

    [Fact]
    public async Task Apply_RefusesATargetThatIsNotDecodableText()
    {
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        File.WriteAllBytes(
            Path.Combine(MonitoredRoot, "src", "Binary.cs"),
            [0x4e, 0x00, 0x4f, 0x0a]);
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var result = await ApplyAsync(workspace, Patch(
            "--- a/src/Binary.cs",
            "+++ b/src/Binary.cs",
            "@@ -1,1 +1,1 @@",
            "-one",
            "+two"));

        Assert.Equal("source_patch_target_binary", result.Code);
    }

    [Fact]
    public async Task Apply_RefusesATargetLargerThanTheReadBoundaryAdmits()
    {
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var result = await ApplyAsync(
            workspace,
            Patch(
                "--- a/src/Checkout.cs",
                "+++ b/src/Checkout.cs",
                "@@ -1,2 +1,2 @@",
                " namespace Shop;",
                "-public class Checkout;",
                "+public sealed class Checkout;"),
            Limits with { MaximumTargetBytes = 8 });

        Assert.Equal("source_patch_target_too_large", result.Code);
    }

    [Fact]
    public async Task Apply_RefusesATargetReachedThroughALink()
    {
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);
        var outside = Path.Combine(temporaryRoot, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(
            Path.Combine(outside, "Secret.cs"),
            "one\n",
            TestContext.Current.CancellationToken);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(workspace.DirectoryPath, "linked"), outside);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            Assert.Skip("Symbolic links are not available to this test process.");
        }

        var result = await ApplyAsync(workspace, Patch(
            "--- a/linked/Secret.cs",
            "+++ b/linked/Secret.cs",
            "@@ -1,1 +1,1 @@",
            "-one",
            "+two"));

        Assert.Equal("source_patch_link_rejected", result.Code);
        Assert.Equal("one\n", await File.ReadAllTextAsync(
            Path.Combine(outside, "Secret.cs"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Apply_LeavesTheWorkspaceExactlyAsItWasWhenTheFifthFileCannotBeWritten()
    {
        // Four files that can be written and a fifth whose path is occupied by a directory. Nothing
        // in a plan can predict every reason a write fails, which is why the commit rolls back; a
        // directory in the way is a deterministic stand-in for a full disk or a revoked permission.
        for (var index = 1; index <= 4; index++)
        {
            Write(MonitoredRoot, $"src/File{index}.cs", $"namespace Shop;\npublic class File{index};\n");
        }

        Directory.CreateDirectory(Path.Combine(MonitoredRoot, "src", "Blocked.cs"));
        using var workspace = await MaterializeAsync(MonitoredRoot);
        var before = Fingerprint(workspace);

        var sections = Enumerable.Range(1, 4).SelectMany(index => new[]
        {
            $"--- a/src/File{index}.cs",
            $"+++ b/src/File{index}.cs",
            "@@ -1,2 +1,2 @@",
            " namespace Shop;",
            $"-public class File{index};",
            $"+public sealed class File{index};",
        }).ToArray();
        var result = await ApplyAsync(workspace, Patch(
        [
            .. sections,
            "--- /dev/null",
            "+++ b/src/Blocked.cs",
            "@@ -0,0 +1,1 @@",
            "+namespace Shop;",
        ]));

        Assert.Equal("source_patch_unavailable", result.Code);
        Assert.Null(result.TreeIdentity);
        Assert.Equal(before, Fingerprint(workspace));
        Assert.Equal(workspace.TreeIdentity, await ScanIdentityAsync(workspace));

        // The same four files without the fifth apply and change the tree, so the run above did
        // write them before it failed, so the equality above is a rollback rather than a plan that
        // refused early and never wrote anything.
        var withoutTheFifth = await ApplyAsync(workspace, Patch(sections));
        Assert.Equal("source_patch_applied", withoutTheFifth.Code);
        Assert.NotEqual(before, Fingerprint(workspace));
    }

    [Fact]
    public async Task Apply_LeavesTheWorkspaceExactlyAsItWasWhenACreatedDirectoryMustBeUndone()
    {
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        Directory.CreateDirectory(Path.Combine(MonitoredRoot, "src", "generated", "Blocked.cs"));
        using var workspace = await MaterializeAsync(MonitoredRoot);
        var before = Fingerprint(workspace);

        var result = await ApplyAsync(workspace, Patch(
            "--- /dev/null",
            "+++ b/src/added/Added.cs",
            "@@ -0,0 +1,1 @@",
            "+namespace Shop;",
            "--- /dev/null",
            "+++ b/src/generated/Blocked.cs",
            "@@ -0,0 +1,1 @@",
            "+namespace Shop;"));

        Assert.Equal("source_patch_unavailable", result.Code);
        Assert.False(Directory.Exists(Path.Combine(workspace.DirectoryPath, "src", "added")));
        Assert.Equal(before, Fingerprint(workspace));
    }

    [Fact]
    public async Task Apply_KeepsTheFileItDeletedWhenACreatedDirectoryTookThePathBack()
    {
        // `src/A.cs` goes, then creating `src/A.cs/C.cs` makes that same name a directory, then the
        // third section fails. Undoing in the wrong order writes the deleted file back onto a
        // directory, which fails, and then removes the directory that is now empty: the file is gone
        // and the caller is told nothing was applied. The patch is built here rather than parsed
        // because the parser refuses paths that nest like this, and the rollback has to be right
        // whether or not it does.
        Write(MonitoredRoot, "src/A.cs", "one\n");
        Directory.CreateDirectory(Path.Combine(MonitoredRoot, "src", "Blocked.cs"));
        using var workspace = await MaterializeAsync(MonitoredRoot);
        var before = Fingerprint(workspace);

        var result = await ApplyAsync(workspace, new SourcePatch(
        [
            Section("src/A.cs", SourcePatchFileKind.Delete, new SourcePatchHunk(
                1, 1, 0, 0, [new SourcePatchLine('-', "one")], false, false)),
            Section("src/A.cs/C.cs", SourcePatchFileKind.Create, new SourcePatchHunk(
                0, 0, 1, 1, [new SourcePatchLine('+', "c")], false, false)),
            Section("src/Blocked.cs", SourcePatchFileKind.Create, new SourcePatchHunk(
                0, 0, 1, 1, [new SourcePatchLine('+', "x")], false, false)),
        ]));

        Assert.Equal("source_patch_unavailable", result.Code);
        Assert.Equal("one\n", ReadWorkspace(workspace, "src/A.cs"));
        Assert.Equal(before, Fingerprint(workspace));
    }

    [Fact]
    public void Restore_ReportsAnOriginalItCouldNotPutBack()
    {
        // A rollback that could not finish must not report the outcome of one that did, because
        // "nothing was applied and the workspace is as it was" and "nothing was applied and the
        // workspace is something else" mean different things to a caller.
        var occupied = Path.Combine(temporaryRoot, "occupied");
        Directory.CreateDirectory(occupied);

        var lost = new SourcePatchPlannedFile(
            "src/A.cs",
            occupied,
            SourcePatchFileKind.Modify,
            OriginalContent: [1, 2, 3],
            ResultContent: [4]);

        Assert.False(SourcePatchApplier.Restore([lost], []));
    }

    [Fact]
    public void Restore_ReportsSuccessWhenTheWriteItCouldNotMakeChangedNothing()
    {
        // The distinction is about the workspace, not about whether a call threw. A write that
        // failed at the open left the file holding exactly what it held, and a delete of a path the
        // commit never managed to write to has nothing to undo.
        var present = Path.Combine(temporaryRoot, "present.cs");
        File.WriteAllBytes(present, [1, 2, 3]);
        var occupied = Path.Combine(temporaryRoot, "occupied-create");
        Directory.CreateDirectory(occupied);

        var kept = new SourcePatchPlannedFile(
            "src/A.cs",
            present,
            SourcePatchFileKind.Modify,
            OriginalContent: [1, 2, 3],
            ResultContent: [4]);
        var neverWritten = new SourcePatchPlannedFile(
            "src/B.cs",
            occupied,
            SourcePatchFileKind.Create,
            OriginalContent: null,
            ResultContent: [4]);

        Assert.True(SourcePatchApplier.Restore([kept, neverWritten], []));
    }

    [Fact]
    public async Task Apply_RefusesAHunkStartThatWouldWrapTheBoundItIsCheckedAgainst()
    {
        // `start + OldCount` in `int` wraps negative for a start this large, and a negative sum is
        // not greater than the line count, so the bound admits the hunk and the read that follows
        // walks off the front of the list. Built here rather than parsed, because the header bound
        // now refuses this from the text and the applier must refuse it anyway.
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);
        var before = Fingerprint(workspace);

        var result = await ApplyAsync(workspace, new SourcePatch(
        [
            Section("src/Checkout.cs", SourcePatchFileKind.Modify, new SourcePatchHunk(
                int.MaxValue,
                2,
                int.MaxValue,
                2,
                [
                    new SourcePatchLine(' ', "namespace Shop;"),
                    new SourcePatchLine('-', "public class Checkout;"),
                    new SourcePatchLine('+', "public sealed class Checkout;"),
                ],
                false,
                false)),
        ]));

        Assert.Equal("source_patch_context_mismatch", result.Code);
        Assert.Equal(before, Fingerprint(workspace));
    }

    [Fact]
    public async Task Apply_RefusesAnAddedLineThatIsNotTextRatherThanCallingItAFilesystemFailure()
    {
        // An unpaired surrogate denotes no character, so encoding it throws, and the exception it
        // throws derives from ArgumentException. Reporting that as a filesystem failure would tell a
        // caller a disk problem ended an attempt that no disk was involved in.
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var result = await ApplyAsync(workspace, new SourcePatch(
        [
            Section("src/Checkout.cs", SourcePatchFileKind.Modify, new SourcePatchHunk(
                1,
                1,
                1,
                1,
                [
                    new SourcePatchLine('-', "namespace Shop;"),
                    new SourcePatchLine('+', "namespace \ud800Shop;"),
                ],
                false,
                false)),
        ]));

        Assert.Equal("source_patch_unencodable", result.Code);
        Assert.Equal(Checkout, ReadWorkspace(workspace, "src/Checkout.cs"));
    }

    [Fact]
    public async Task Apply_RefusesAPathThatDiffersFromTheWorkspaceOnlyInCase()
    {
        // `File.Exists` is case-insensitive on Windows and case-sensitive on Linux, so this
        // modification changed the existing file on one and was refused as missing on the other, and
        // the creation below was refused as existing on one and produced a second file on the other.
        // Both are refused on both, for the reason two sections differing only in case are.
        Write(MonitoredRoot, "src/checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);
        var before = Fingerprint(workspace);

        var modify = await ApplyAsync(workspace, Patch(
            "--- a/src/Checkout.cs",
            "+++ b/src/Checkout.cs",
            "@@ -1,2 +1,2 @@",
            " namespace Shop;",
            "-public class Checkout;",
            "+public sealed class Checkout;"));

        var create = await ApplyAsync(workspace, Patch(
            "--- /dev/null",
            "+++ b/src/Checkout.cs",
            "@@ -0,0 +1,1 @@",
            "+namespace Shop;"));

        var directory = await ApplyAsync(workspace, Patch(
            "--- /dev/null",
            "+++ b/SRC/Added.cs",
            "@@ -0,0 +1,1 @@",
            "+namespace Shop;"));

        Assert.Equal("source_patch_path_case_mismatch", modify.Code);
        Assert.Equal("source_patch_path_case_mismatch", create.Code);
        Assert.Equal("source_patch_path_case_mismatch", directory.Code);
        Assert.Equal(before, Fingerprint(workspace));
    }

    [Fact]
    public async Task Apply_RefusesANoNewlineMarkerOnAHunkThatDoesNotReachTheEndOfTheFile()
    {
        // The marker says the base ends after the line it follows. Here the base has another line
        // after it, so the marker describes an end that is not there; accepting it and ignoring it
        // let a patch make a claim about the file's last byte that nothing checked.
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);

        var result = await ApplyAsync(workspace, Patch(
            "--- a/src/Checkout.cs",
            "+++ b/src/Checkout.cs",
            "@@ -1,1 +1,1 @@",
            "-namespace Shop;",
            @"\ No newline at end of file",
            "+namespace Warehouse;"));

        Assert.Equal("source_patch_context_mismatch", result.Code);
        Assert.Equal(Checkout, ReadWorkspace(workspace, "src/Checkout.cs"));
    }

    [Fact]
    public async Task Apply_RefusesAPatchThatDoesNotParseWithoutTouchingTheWorkspace()
    {
        Write(MonitoredRoot, "src/Checkout.cs", Checkout);
        using var workspace = await MaterializeAsync(MonitoredRoot);
        var before = Fingerprint(workspace);

        var parsed = SourcePatchParser.Parse("not a patch", Limits);

        Assert.Equal("source_patch_malformed", parsed.Code);
        Assert.Null(parsed.Patch);
        Assert.Equal(before, Fingerprint(workspace));
    }

    public void Dispose()
    {
        Directory.Delete(temporaryRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Patch(params string[] lines) => string.Join('\n', lines) + "\n";

    /// <summary>
    /// One file section, built rather than parsed. Used only where the parser would refuse the
    /// patch and the applier still has to behave, so that a rollback or a bound is proved to hold on
    /// its own rather than because something upstream happened to hold first.
    /// </summary>
    private static SourcePatchFile Section(string path, SourcePatchFileKind kind, SourcePatchHunk hunk) =>
        new(path, kind, [hunk]);

    private async Task<SourceWorkspace> MaterializeAsync(string sourceRoot)
    {
        var result = await new SourceWorkspaceMaterializer(WorkspaceRoot, SourceWorkspaceBounds.Default)
            .MaterializeAsync(sourceRoot, TestContext.Current.CancellationToken);
        Assert.Equal("source_workspace_created", result.Code);
        Assert.NotNull(result.Workspace);
        return result.Workspace;
    }

    private static async Task<SourcePatchApplyResult> ApplyAsync(
        SourceWorkspace workspace,
        string patch,
        SourcePatchLimits? limits = null)
    {
        var effective = limits ?? Limits;
        var parsed = SourcePatchParser.Parse(patch, effective);
        Assert.Equal("source_patch_parsed", parsed.Code);
        Assert.NotNull(parsed.Patch);
        return await new SourcePatchApplier(workspace.DirectoryPath, effective, SourceWorkspaceBounds.Default)
            .ApplyAsync(parsed.Patch, TestContext.Current.CancellationToken);
    }

    private static async Task<SourcePatchApplyResult> ApplyAsync(SourceWorkspace workspace, SourcePatch patch) =>
        await new SourcePatchApplier(workspace.DirectoryPath, Limits, SourceWorkspaceBounds.Default)
            .ApplyAsync(patch, TestContext.Current.CancellationToken);

    private static async Task<string?> ScanIdentityAsync(SourceWorkspace workspace)
    {
        var scan = await SourceTreeScanner.ScanAsync(
            workspace.DirectoryPath,
            SourceWorkspaceBounds.Default,
            TestContext.Current.CancellationToken);
        return scan.Entries is null ? null : SourceTreeIdentity.Compute(scan.Entries);
    }

    private static string ReadWorkspace(SourceWorkspace workspace, string relativePath) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(
            Path.Combine(workspace.DirectoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar))));

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
    }

    /// <summary>
    /// An independent description of the workspace: every directory and every file with its exact
    /// bytes, built here rather than through the tree identity so that a change breaking both the
    /// applier and the identity together could not hide in it, and so that an empty directory a
    /// rollback failed to remove is visible even though no identity covers one.
    /// </summary>
    private static string Fingerprint(SourceWorkspace workspace)
    {
        var lines = Directory
            .EnumerateFileSystemEntries(workspace.DirectoryPath, "*", SearchOption.AllDirectories)
            .Select(entry => Directory.Exists(entry)
                ? "dir " + Path.GetRelativePath(workspace.DirectoryPath, entry).Replace('\\', '/')
                : "file " + Path.GetRelativePath(workspace.DirectoryPath, entry).Replace('\\', '/') + " " +
                  Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(entry))))
            .Order(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }
}
