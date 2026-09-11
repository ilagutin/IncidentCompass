using IncidentCompass.Application.Remediation;

namespace IncidentCompass.UnitTests;

/// <summary>
/// What counts as "a unified diff and nothing else" when a model answers, and what does not.
/// </summary>
/// <remarks>
/// The instruction is exact and this is what enforces it. The one accommodation is the code fence,
/// because providers add one whatever the instruction says and the fence lines are not diff syntax,
/// so without unwrapping the ordinary well-meant answer would be refused for a mistake the model did
/// not make. Everything else is refused, and the bytes that survive unwrapping are the bytes that
/// get parsed, applied and recorded.
/// </remarks>
public sealed class RemediationPatchAnswerTests
{
    private const string Diff =
        "--- a/src/Checkout.cs\n+++ b/src/Checkout.cs\n@@ -1,1 +1,1 @@\n-old\n+new\n";

    [Fact]
    public void Extract_AcceptsABareDiff() =>
        Assert.Equal(Diff, RemediationPatchAnswer.Extract(Diff));

    [Fact]
    public void Extract_AcceptsADiffGitHeader() =>
        Assert.StartsWith(
            "diff --git ",
            RemediationPatchAnswer.Extract("diff --git a/x.cs b/x.cs\n" + Diff),
            StringComparison.Ordinal);

    [Theory]
    [InlineData("```")]
    [InlineData("```diff")]
    [InlineData("```patch")]
    [InlineData("```DIFF")]
    public void Extract_UnwrapsOneFencedBlock(string opener) =>
        Assert.Equal(Diff, RemediationPatchAnswer.Extract(opener + "\n" + Diff + "```\n"));

    [Fact]
    public void Extract_IgnoresBlankLinesAroundTheAnswerAndAroundTheBlock() =>
        Assert.Equal(Diff, RemediationPatchAnswer.Extract("\n\n```diff\n\n" + Diff + "\n\n```\n\n"));

    [Theory]
    // Prose, which is the shape of a refusal or an apology.
    [InlineData("I could not find a fix for this incident.")]
    // A heading before the fence. The answer is a diff and something else.
    [InlineData("Here is the fix:\n```diff\n--- a/x.cs\n+++ b/x.cs\n@@ -1,1 +1,1 @@\n-a\n+b\n```")]
    // Commentary after the fence, which is the same thing on the other side.
    [InlineData("```diff\n--- a/x.cs\n+++ b/x.cs\n@@ -1,1 +1,1 @@\n-a\n+b\n```\nLet me know if this helps.")]
    // An unclosed block: there is no way to tell where the diff was meant to stop.
    [InlineData("```diff\n--- a/x.cs\n+++ b/x.cs\n@@ -1,1 +1,1 @@\n-a\n+b\n")]
    // An info string naming something that is not a diff.
    [InlineData("```csharp\n--- a/x.cs\n+++ b/x.cs\n@@ -1,1 +1,1 @@\n-a\n+b\n```")]
    // A fenced block holding something that does not open a diff section.
    [InlineData("```diff\n@@ -1,1 +1,1 @@\n-a\n+b\n```")]
    // JSON, which is what a model trained on the worker roles may reach for.
    [InlineData("{\"patch\": \"--- a/x.cs\"}")]
    [InlineData("")]
    [InlineData("   \n\n ")]
    [InlineData(null)]
    public void Extract_RefusesAnythingThatIsNotADiffAndNothingElse(string? answer) =>
        Assert.Null(RemediationPatchAnswer.Extract(answer));

    /// <summary>
    /// A carriage return survives unwrapping exactly where it was.
    /// </summary>
    /// <remarks>
    /// A body line carries a file's exact bytes, and both the context check and the tree identity
    /// treat a carriage return as part of the base. Normalizing here would let a diff written for a
    /// Windows checkout match a Unix one, which is the single thing the byte-exact context check
    /// exists to prevent. It is not tidied even though tidying would make more answers apply.
    /// </remarks>
    [Fact]
    public void Extract_LeavesCarriageReturnsWhereTheyAre()
    {
        var windows = "--- a/x.cs\r\n+++ b/x.cs\r\n@@ -1,1 +1,1 @@\r\n-a\r\n+b\r\n";

        var extracted = RemediationPatchAnswer.Extract("```diff\n" + windows + "```\n");

        Assert.Equal(windows, extracted);
    }
}
