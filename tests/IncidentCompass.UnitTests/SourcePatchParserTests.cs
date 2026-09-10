using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

/// <summary>
/// What the parser accepts and, at much greater length, what it refuses. Nothing here touches a
/// filesystem: every case is a property of the text, so every refusal is reproducible from the patch
/// alone and none of them depends on what a workspace happens to contain.
/// </summary>
public sealed class SourcePatchParserTests
{
    private static readonly SourcePatchLimits Limits =
        SourcePatchLimits.For(new SourceContextOptions(), SourceWorkspaceBounds.Default);

    [Fact]
    public void Parse_ReadsAModificationACreationAndADeletion()
    {
        var result = SourcePatchParser.Parse(
            Patch(
                "diff --git a/src/Checkout.cs b/src/Checkout.cs",
                "index 1a2b3c4..5d6e7f8 100644",
                "--- a/src/Checkout.cs",
                "+++ b/src/Checkout.cs",
                "@@ -1,3 +1,3 @@ public class Checkout",
                " namespace Shop;",
                "-public class Checkout;",
                "+public sealed class Checkout;",
                " ",
                "diff --git a/src/Discount.cs b/src/Discount.cs",
                "new file mode 100644",
                "--- /dev/null",
                "+++ b/src/Discount.cs",
                "@@ -0,0 +1,1 @@",
                "+public class Discount;",
                "diff --git a/src/Legacy.cs b/src/Legacy.cs",
                "deleted file mode 100644",
                "--- a/src/Legacy.cs",
                "+++ /dev/null",
                "@@ -1,1 +0,0 @@",
                "-public class Legacy;"),
            Limits);

        Assert.Equal("source_patch_parsed", result.Code);
        Assert.NotNull(result.Patch);
        Assert.Equal(3, result.Patch.Files.Count);
        Assert.Equal(SourcePatchFileKind.Modify, result.Patch.Files[0].Kind);
        Assert.Equal("src/Checkout.cs", result.Patch.Files[0].RepositoryPath);
        Assert.Equal(SourcePatchFileKind.Create, result.Patch.Files[1].Kind);
        Assert.Equal(SourcePatchFileKind.Delete, result.Patch.Files[2].Kind);
    }

    [Fact]
    public void Parse_KeepsACarriageReturnOnABodyLineAndIgnoresOneOnAStructuralLine()
    {
        // The whole patch is transported with Windows terminators. The hunk header is structural, so
        // its carriage return is dropped; the context line is file content, so its carriage return is
        // part of the base it will be compared against.
        var result = SourcePatchParser.Parse(
            "--- a/src/Checkout.cs\r\n+++ b/src/Checkout.cs\r\n@@ -1,1 +1,1 @@\r\n-old\r\n+new\r\n",
            Limits);

        Assert.NotNull(result.Patch);
        var lines = result.Patch.Files[0].Hunks[0].Lines;
        Assert.Equal("old\r", lines[0].Text);
        Assert.Equal("new\r", lines[1].Text);
    }

    [Fact]
    public void Parse_CarriesTheNoNewlineMarkerForBothSides()
    {
        var result = SourcePatchParser.Parse(
            Patch(
                "--- a/src/Checkout.cs",
                "+++ b/src/Checkout.cs",
                "@@ -1,1 +1,1 @@",
                "-old",
                @"\ No newline at end of file",
                "+new"),
            Limits);

        Assert.NotNull(result.Patch);
        var hunk = result.Patch.Files[0].Hunks[0];
        Assert.True(hunk.OldEndsWithoutNewline);
        Assert.False(hunk.NewEndsWithoutNewline);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t\n")]
    public void Parse_RefusesAnEmptyPatch(string patch) =>
        Assert.Equal("source_patch_empty", SourcePatchParser.Parse(patch, Limits).Code);

    [Fact]
    public void Parse_RefusesAPatchOverTheRawBudget()
    {
        var patch = Patch(
            "--- a/src/Checkout.cs",
            "+++ b/src/Checkout.cs",
            "@@ -1,1 +1,1 @@",
            "-old",
            "+new");

        Assert.Equal(
            "source_patch_too_large",
            SourcePatchParser.Parse(patch, Limits with { MaximumPatchBytes = 16 }).Code);
    }

    [Theory]
    // Nothing that starts a file section.
    [InlineData("please apply this urgently")]
    // A file header with no counterpart, and a counterpart with no hunks after it.
    [InlineData("--- a/src/Checkout.cs")]
    // No prefix, which is the `--no-prefix` form: refused rather than guessed at.
    [InlineData("--- src/Checkout.cs\n+++ src/Checkout.cs\n@@ -1,1 +1,1 @@\n-a\n+b")]
    // A quoted path, the form git uses when a name needs escaping.
    [InlineData("--- \"a/src/Checkout.cs\"\n+++ \"b/src/Checkout.cs\"\n@@ -1,1 +1,1 @@\n-a\n+b")]
    // Both sides absent, which names no file at all.
    [InlineData("--- /dev/null\n+++ /dev/null\n@@ -0,0 +1,1 @@\n+a")]
    // A NUL anywhere in the text.
    [InlineData("--- a/src/Check\0out.cs\n+++ b/src/Checkout.cs\n@@ -1,1 +1,1 @@\n-a\n+b")]
    // An extended header nothing recognizes.
    [InlineData("diff --git a/src/Checkout.cs b/src/Checkout.cs\nmagic header\n--- a/src/Checkout.cs\n+++ b/src/Checkout.cs\n@@ -1,1 +1,1 @@\n-a\n+b")]
    public void Parse_RefusesTextOutsideTheAcceptedSubset(string patch) =>
        Assert.Equal("source_patch_malformed", SourcePatchParser.Parse(patch, Limits).Code);

    [Theory]
    // A rename, expressed both ways git can express one.
    [InlineData("diff --git a/src/Old.cs b/src/New.cs\n--- a/src/Old.cs\n+++ b/src/New.cs\n@@ -1,1 +1,1 @@\n-a\n+b")]
    [InlineData("diff --git a/src/Old.cs b/src/New.cs\nsimilarity index 90%\nrename from src/Old.cs\nrename to src/New.cs\n--- a/src/Old.cs\n+++ b/src/New.cs\n@@ -1,1 +1,1 @@\n-a\n+b")]
    // A copy.
    [InlineData("diff --git a/src/A.cs b/src/A.cs\ncopy from src/Other.cs\ncopy to src/A.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,1 @@\n-a\n+b")]
    // A mode change with no content change, which a content-only applier cannot represent.
    [InlineData("diff --git a/src/A.cs b/src/A.cs\nold mode 100644\nnew mode 100755\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,1 @@\n-a\n+b")]
    // A create that asks for an executable file, and one that asks for a symbolic link.
    [InlineData("diff --git a/src/A.cs b/src/A.cs\nnew file mode 100755\n--- /dev/null\n+++ b/src/A.cs\n@@ -0,0 +1,1 @@\n+a")]
    [InlineData("diff --git a/src/A.cs b/src/A.cs\nnew file mode 120000\n--- /dev/null\n+++ b/src/A.cs\n@@ -0,0 +1,1 @@\n+../../etc/passwd")]
    // A gitlink, which is how a submodule is recorded.
    [InlineData("diff --git a/src/A.cs b/src/A.cs\nnew file mode 160000\n--- /dev/null\n+++ b/src/A.cs\n@@ -0,0 +1,1 @@\n+a")]
    public void Parse_RefusesOperationsItDoesNotImplement(string patch) =>
        Assert.Equal("source_patch_unsupported_operation", SourcePatchParser.Parse(patch, Limits).Code);

    [Theory]
    [InlineData("diff --git a/src/A.cs b/src/A.cs\nGIT binary patch\nliteral 12\n--- a/src/A.cs\n+++ b/src/A.cs")]
    [InlineData("diff --git a/src/A.cs b/src/A.cs\nBinary files a/src/A.cs and b/src/A.cs differ\n--- a/src/A.cs\n+++ b/src/A.cs")]
    public void Parse_RefusesABinaryPatch(string patch) =>
        Assert.Equal("source_patch_binary", SourcePatchParser.Parse(patch, Limits).Code);

    [Fact]
    public void Parse_RefusesAFileSectionWithNoHunks() =>
        Assert.Equal(
            "source_patch_no_hunks",
            SourcePatchParser.Parse(Patch("--- a/src/A.cs", "+++ b/src/A.cs"), Limits).Code);

    [Theory]
    // The header promises three base lines and the body supplies two.
    [InlineData("@@ -1,3 +1,3 @@\n one\n-two\n+three")]
    // The header promises one and the body supplies two.
    [InlineData("@@ -1,1 +1,2 @@\n one\n-two\n+three\n+four")]
    public void Parse_RefusesAHunkWhoseBodyDisagreesWithItsHeader(string hunk) =>
        Assert.Equal(
            "source_patch_hunk_count_mismatch",
            SourcePatchParser.Parse("--- a/src/A.cs\n+++ b/src/A.cs\n" + hunk, Limits).Code);

    [Fact]
    public void Parse_RefusesOverlappingHunks() =>
        Assert.Equal(
            "source_patch_hunk_overlap",
            SourcePatchParser.Parse(
                Patch(
                    "--- a/src/A.cs",
                    "+++ b/src/A.cs",
                    "@@ -1,2 +1,2 @@",
                    " one",
                    "-two",
                    "+three",
                    "@@ -2,2 +2,2 @@",
                    " two",
                    "-three",
                    "+four"),
                Limits).Code);

    [Fact]
    public void Parse_RefusesAHunkHeaderWhoseResultStartContradictsTheHunksBeforeIt() =>
        // Without this the result side of a header is decoration, and a patch could claim any
        // numbering it liked while still parsing.
        Assert.Equal(
            "source_patch_malformed",
            SourcePatchParser.Parse(
                Patch("--- a/src/A.cs", "+++ b/src/A.cs", "@@ -1,2 +5,2 @@", " one", "-two", "+three"),
                Limits).Code);

    [Theory]
    // A hunk that consumes nothing and produces nothing.
    [InlineData("@@ -1,0 +1,0 @@")]
    // A body line with no origin character at all.
    [InlineData("@@ -1,1 +1,1 @@\n\n+b")]
    // A body line whose origin is not one of the three.
    [InlineData("@@ -1,1 +1,1 @@\n*old\n+new")]
    // A marker that is not the marker.
    [InlineData("@@ -1,1 +1,1 @@\n-old\n\\ No newline\n+new")]
    // A marker before any line it could attach to.
    [InlineData("@@ -1,1 +1,1 @@\n\\ No newline at end of file\n-old\n+new")]
    // A base-side line after the base side was closed by a marker.
    [InlineData("@@ -2,2 +1,1 @@\n-one\n\\ No newline at end of file\n-two\n+new")]
    // Counts that are not counts.
    [InlineData("@@ -x,1 +1,1 @@\n-old\n+new")]
    [InlineData("@@ -1,-1 +1,1 @@\n-old\n+new")]
    [InlineData("@@ -0,1 +1,1 @@\n-old\n+new")]
    [InlineData("@@ -1,1 +1,1 @\n-old\n+new")]
    public void Parse_RefusesAMalformedHunk(string hunk) =>
        Assert.Equal(
            "source_patch_malformed",
            SourcePatchParser.Parse("--- a/src/A.cs\n+++ b/src/A.cs\n" + hunk, Limits).Code);

    [Theory]
    // A create that quotes a base it says does not exist.
    [InlineData("--- /dev/null\n+++ b/src/A.cs\n@@ -1,1 +1,2 @@\n-old\n+new\n+more")]
    // A delete that does not start at the first line, so it cannot have quoted the whole file.
    [InlineData("--- a/src/A.cs\n+++ /dev/null\n@@ -2,1 +0,0 @@\n-two")]
    // A create in two hunks, which no create ever is.
    [InlineData("--- /dev/null\n+++ b/src/A.cs\n@@ -0,0 +1,1 @@\n+one\n@@ -0,0 +2,1 @@\n+two")]
    // Headers whose two halves describe different operations.
    [InlineData("diff --git a/src/A.cs b/src/A.cs\nnew file mode 100644\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,1 @@\n-a\n+b")]
    [InlineData("diff --git a/src/A.cs b/src/A.cs\ndeleted file mode 100644\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,1 @@\n-a\n+b")]
    // A `diff --git` line naming a path the file headers do not.
    [InlineData("diff --git a/src/Other.cs b/src/Other.cs\n--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1,1 +1,1 @@\n-a\n+b")]
    public void Parse_RefusesASectionWhoseHalvesDisagree(string patch) =>
        Assert.Equal("source_patch_malformed", SourcePatchParser.Parse(patch, Limits).Code);

    [Theory]
    [InlineData("src/A.cs", "src/A.cs")]
    // Two sections that are one file on Windows and two on Linux are refused on both.
    [InlineData("src/A.cs", "src/a.cs")]
    public void Parse_RefusesTwoSectionsForOnePath(string first, string second) =>
        Assert.Equal(
            "source_patch_duplicate_path",
            SourcePatchParser.Parse(
                Patch(
                    "--- a/" + first,
                    "+++ b/" + first,
                    "@@ -1,1 +1,1 @@",
                    "-one",
                    "+two",
                    "--- a/" + second,
                    "+++ b/" + second,
                    "@@ -1,1 +1,1 @@",
                    "-three",
                    "+four"),
                Limits).Code);

    [Theory]
    // The file the first section removes is the directory the second section needs.
    [InlineData("src/A.cs", "src/A.cs/C.cs")]
    // The same conflict in the other order.
    [InlineData("src/A.cs/C.cs", "src/A.cs")]
    // And the same one spelled so that it is one name on Windows and two on Linux.
    [InlineData("src/a.cs", "src/A.cs/C.cs")]
    public void Parse_RefusesTwoSectionsWhereOnePathIsADirectoryPrefixOfTheOther(string first, string second) =>
        // Such a patch asks for one name to be a file and a directory at once, which is coherent in
        // one order and destructive in the other, and the format does not say which order applies.
        Assert.Equal(
            "source_patch_path_conflict",
            SourcePatchParser.Parse(
                Patch(
                    "--- a/" + first,
                    "+++ /dev/null",
                    "@@ -1,1 +0,0 @@",
                    "-one",
                    "--- /dev/null",
                    "+++ b/" + second,
                    "@@ -0,0 +1,1 @@",
                    "+two"),
                Limits).Code);

    [Fact]
    public void Parse_AdmitsTwoSectionsThatOnlyShareASegmentPrefix() =>
        // `src/A.cs` and `src/A.cs2.cs` share a prefix of characters and no directory, so nothing
        // about them is ambiguous and the conflict rule must not reach them.
        Assert.Equal(
            "source_patch_parsed",
            SourcePatchParser.Parse(
                Patch(
                    "--- a/src/A.cs",
                    "+++ b/src/A.cs",
                    "@@ -1,1 +1,1 @@",
                    "-one",
                    "+two",
                    "--- a/src/A.cs2.cs",
                    "+++ b/src/A.cs2.cs",
                    "@@ -1,1 +1,1 @@",
                    "-three",
                    "+four"),
                Limits).Code);

    [Theory]
    // A high surrogate with a non-surrogate after it, one with nothing after it, and a lone low one.
    // Built from code points rather than written into the attribute: an attribute argument is stored
    // as UTF-8 in metadata, and a lone surrogate does not survive that, so an inline literal would
    // arrive here as a replacement character and the test would assert nothing.
    [InlineData(0xD800, "o")]
    [InlineData(0xD800, "")]
    [InlineData(0xDC00, "o")]
    public void Parse_RefusesTextHoldingAnUnpairedSurrogate(int codeUnit, string suffix) =>
        // It denotes no character, so no file could hold it. Encoding it throws an exception that
        // derives from ArgumentException, which is how this used to reach the applier disguised as a
        // filesystem failure.
        Assert.Equal(
            "source_patch_unencodable",
            SourcePatchParser.Parse(
                Patch("--- a/src/A.cs", "+++ b/src/A.cs", "@@ -1,1 +1,1 @@", "-one", "+tw" + (char)codeUnit + suffix),
                Limits).Code);

    [Fact]
    public void Parse_AdmitsAPairedSurrogateInABodyLine() =>
        // A body line carries the file's exact characters, and an astral character is one of them.
        // It is also the case the refusal above must not reach: the pair denotes a character, and a
        // check that walked the string one UTF-16 unit at a time without pairing them would refuse
        // every file that holds one.
        Assert.Equal(
            "source_patch_parsed",
            SourcePatchParser.Parse(
                Patch(
                    "--- a/src/A.cs",
                    "+++ b/src/A.cs",
                    "@@ -1,1 +1,1 @@",
                    "-one",
                    "+tw" + char.ConvertFromUtf32(0x1F600) + "o"),
                Limits).Code);

    [Theory]
    // A start no file this admits could have. Left to the applier it reported a mismatch against a
    // base, which is a statement about a file; it is a statement about the header's arithmetic.
    [InlineData("@@ -2147483647,2 +2147483647,2 @@\n one\n-two\n+three")]
    [InlineData("@@ -2147483647,1 +2147483647,1 @@\n-two\n+three")]
    public void Parse_RefusesAHunkHeaderNamingALineNoAdmissibleFileCouldHold(string hunk) =>
        Assert.Equal(
            "source_patch_malformed",
            SourcePatchParser.Parse("--- a/src/A.cs\n+++ b/src/A.cs\n" + hunk, Limits).Code);

    [Fact]
    public void Parse_AdmitsAHunkHeaderAtTheHighestLineAnAdmissibleFileCouldHold()
    {
        // A file of n bytes holds at most n lines, so the bound is the size bound and the line right
        // at it is still a line a file could have.
        var start = Limits.MaximumTargetBytes;

        Assert.Equal(
            "source_patch_parsed",
            SourcePatchParser.Parse(
                $"--- a/src/A.cs\n+++ b/src/A.cs\n@@ -{start},1 +{start},1 @@\n-one\n+two\n",
                Limits).Code);
    }

    [Fact]
    public void Parse_RefusesMoreFilesThanTheBoundAdmits()
    {
        var sections = Enumerable
            .Range(0, Limits.MaximumFiles + 1)
            .SelectMany(index => new[]
            {
                $"--- a/src/File{index}.cs",
                $"+++ b/src/File{index}.cs",
                "@@ -1,1 +1,1 @@",
                "-one",
                "+two",
            });

        Assert.Equal("source_patch_file_limit", SourcePatchParser.Parse(Patch([.. sections]), Limits).Code);
    }

    [Fact]
    public void Parse_RefusesMoreHunksThanTheBoundAdmits()
    {
        var hunks = Enumerable
            .Range(1, Limits.MaximumHunksPerFile + 1)
            .SelectMany(line => new[] { $"@@ -{line},1 +{line},1 @@", $"-old{line}", $"+new{line}" });
        Assert.Equal(
            "source_patch_hunk_limit",
            SourcePatchParser.Parse(Patch(["--- a/src/A.cs", "+++ b/src/A.cs", .. hunks]), Limits).Code);
    }

    [Theory]
    [InlineData("src/../../etc/passwd.cs", "source_patch_path_rejected")]
    [InlineData("/etc/passwd.cs", "source_patch_path_rejected")]
    // The classic diff timestamp field, which arrives as part of the path and is refused as one
    // rather than trimmed off and ignored.
    [InlineData("src/Checkout.cs\t2026-01-01", "source_patch_path_rejected")]
    [InlineData(".env.cs", "source_patch_secret_path_rejected")]
    [InlineData("README.md", "source_patch_extension_rejected")]
    public void Parse_AppliesThePathPolicyToEverySectionItReads(string path, string expected) =>
        Assert.Equal(
            expected,
            SourcePatchParser.Parse(
                Patch("--- a/" + path, "+++ b/" + path, "@@ -1,1 +1,1 @@", "-one", "+two"),
                Limits).Code);

    private static string Patch(params string[] lines) => string.Join('\n', lines) + "\n";
}
