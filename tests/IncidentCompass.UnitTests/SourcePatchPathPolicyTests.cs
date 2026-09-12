using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Every path a patch may not name, asserted as a decision rather than as a filesystem effect.
/// </summary>
/// <remarks>
/// These cannot skip and do not depend on the platform. That matters more here than anywhere else in
/// the patch path: half of the escapes below are escapes on exactly one operating system, and a test
/// that only proved the rule where the escape does not work would prove nothing about the machine
/// that runs the worker.
/// </remarks>
public sealed class SourcePatchPathPolicyTests
{
    private static readonly SourcePatchLimits Limits =
        SourcePatchLimits.For(new SourceContextOptions(), SourceWorkspaceBounds.Default);

    [Theory]
    [InlineData("src/Checkout.cs")]
    [InlineData("Checkout.cs")]
    [InlineData("src/nested/deeper/Checkout.CS")]
    [InlineData("src/.hidden/Checkout.cs")]
    public void Reject_AdmitsAnOrdinarySourcePath(string path) =>
        Assert.Null(SourcePatchPathPolicy.Reject(path, Limits));

    [Theory]
    // Traversal, spelled out and hidden behind a segment that looks like a directory.
    [InlineData("../Checkout.cs")]
    [InlineData("src/../../Checkout.cs")]
    [InlineData("src/./Checkout.cs")]
    // Absolute, on either platform's spelling, and the UNC prefix that names another host.
    [InlineData("/etc/Checkout.cs")]
    [InlineData("//host/share/Checkout.cs")]
    [InlineData("C:/temp/Checkout.cs")]
    [InlineData("C:\\temp\\Checkout.cs")]
    // A separator Windows honours and Linux does not, which is how one path means two trees.
    [InlineData("src\\..\\..\\Checkout.cs")]
    [InlineData("src\\Checkout.cs")]
    // An NTFS alternate data stream hanging off an admissible name.
    [InlineData("src/Checkout.cs:stream")]
    // A NUL, which truncates the name for anything that reaches a C string.
    [InlineData("src/Checkout.cs\0.txt")]
    [InlineData("src/Check\0out.cs")]
    // Control characters and whitespace, which make the path a reviewer reads differ from the path
    // that is opened.
    [InlineData("src/Check\tout.cs")]
    [InlineData("src/Check out.cs")]
    [InlineData("src/Checkout\r\n.cs")]
    // Empty and trailing segments, which name no file.
    [InlineData("src//Checkout.cs")]
    [InlineData("src/Checkout.cs/")]
    [InlineData("")]
    // Windows strips a trailing dot when opening, so this and `Checkout.cs` are one file there.
    [InlineData("src/Checkout.cs.")]
    // Device names, which resolve whatever directory they appear in.
    [InlineData("src/nul.cs")]
    [InlineData("src/CON.cs")]
    [InlineData("com1.cs")]
    // Repository metadata and the marker that declares a submodule.
    [InlineData(".git/config.cs")]
    [InlineData("src/.GIT/hooks.cs")]
    [InlineData(".gitmodules")]
    public void Reject_RefusesEveryEscapeAndRewrite(string path) =>
        Assert.Equal("source_patch_path_rejected", SourcePatchPathPolicy.Reject(path, Limits));

    [Theory]
    // Written as escapes rather than as the characters themselves, because a source file carrying
    // them literally would misrender for the next person to read this test, which is the complaint.
    // A right-to-left override and a left-to-right one, which reverse what a reviewer reads while
    // leaving the bytes that are opened untouched. This is the Trojan Source shape, and
    // `char.IsControl` does not see it: these are category Cf, not Cc.
    [InlineData("src/\u202ECheckout.cs")]
    [InlineData("src/\u202DCheckout.cs")]
    // A right-to-left isolate, the newer spelling of the same trick.
    [InlineData("src/\u2067Checkout.cs")]
    // A zero-width space and a zero-width joiner, which are segment boundaries nobody can see.
    [InlineData("src/Check\u200Bout.cs")]
    [InlineData("src/Check\u200Dout.cs")]
    // A byte-order mark in the middle of a name, which renders as nothing at all.
    [InlineData("src/\uFEFFCheckout.cs")]
    // A non-breaking space, which reads as the space this already refuses and is not one.
    [InlineData("src/Check\u00A0out.cs")]
    // Fullwidth homoglyphs of the separator, the drive separator and the escape.
    [InlineData("src\uFF0FCheckout.cs")]
    [InlineData("src/Checkout.cs\uFF1Astream")]
    [InlineData("src\uFF3CCheckout.cs")]
    // The characters Windows rejects in a file name, refused everywhere for the reason the device
    // names are: a name one platform accepts and the other does not means two different trees.
    [InlineData("src/Check*out.cs")]
    [InlineData("src/Check?out.cs")]
    [InlineData("src/Check|out.cs")]
    [InlineData("src/Check<out>.cs")]
    public void Reject_RefusesAPathAReviewerCannotReadAsWhatIsOpened(string path) =>
        // The stated reason the path is checked at all is that a human approves a diff by reading
        // it. A character that is invisible, that moves what follows it, or that is a homoglyph of a
        // separator defeats exactly that, so the admitted set is printable ASCII and nothing else.
        Assert.Equal("source_patch_path_rejected", SourcePatchPathPolicy.Reject(path, Limits));

    [Fact]
    public void Reject_RefusesAPathDeeperThanTheTreeWalkAdmits()
    {
        // Depth is a property of the text, so it is refused from the text. Left to the walk that
        // recomputes the identity, the file was created, the walk refused the tree it produced, the
        // whole attempt was rolled back, and the caller was handed a workspace code for what was
        // only ever an inadmissible path.
        var directories = Enumerable.Range(0, Limits.MaximumPathSegments - 1).Select(index => $"d{index}");
        var atTheBound = string.Join('/', [.. directories, "A.cs"]);

        Assert.Null(SourcePatchPathPolicy.Reject(atTheBound, Limits));
        Assert.Equal("source_patch_path_rejected", SourcePatchPathPolicy.Reject("deeper/" + atTheBound, Limits));
    }

    [Fact]
    public void Reject_RefusesAPathLongerThanTheBound() =>
        Assert.Equal(
            "source_patch_path_rejected",
            SourcePatchPathPolicy.Reject(new string('a', Limits.MaximumPathCharacters) + ".cs", Limits));

    [Theory]
    // Named credentials, including the suffixed forms a generator produces.
    [InlineData(".env")]
    [InlineData("deploy/.env.production")]
    [InlineData("config/.env.cs")]
    [InlineData("id_rsa")]
    [InlineData("keys/id_ed25519.cs")]
    [InlineData(".netrc")]
    [InlineData("home/.npmrc")]
    [InlineData(".git-credentials")]
    // Key material by extension.
    [InlineData("certs/server.pem")]
    [InlineData("certs/server.key")]
    [InlineData("certs/server.pfx")]
    // Directories whose contents are credentials by convention, even with an admitted extension.
    [InlineData(".ssh/config.cs")]
    [InlineData("home/.aws/credentials.cs")]
    public void Reject_RefusesCredentialShapedPaths(string path) =>
        Assert.Equal("source_patch_secret_path_rejected", SourcePatchPathPolicy.Reject(path, Limits));

    [Theory]
    [InlineData("README.md")]
    [InlineData("src/Checkout.csproj")]
    [InlineData("src/Checkout")]
    [InlineData("Directory.Build.props")]
    public void Reject_RefusesWhatTheSourceReadBoundaryCouldNotQuoteBack(string path) =>
        // A change to a file the excerpt reader will not open produces evidence nothing can cite, so
        // the change is refused rather than made uncitable.
        Assert.Equal("source_patch_extension_rejected", SourcePatchPathPolicy.Reject(path, Limits));

    [Fact]
    public void Reject_FollowsTheConfiguredExtensionSetRatherThanAFixedOne()
    {
        var widened = Limits with { AllowedExtensions = [".cs", ".csproj"] };

        Assert.Null(SourcePatchPathPolicy.Reject("src/Checkout.csproj", widened));
        Assert.Equal("source_patch_extension_rejected", SourcePatchPathPolicy.Reject("README.md", widened));
    }
}
