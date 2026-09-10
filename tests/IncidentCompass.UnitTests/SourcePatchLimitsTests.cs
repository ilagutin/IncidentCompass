using System.Text;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.ActionApprovals;
using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The raw patch budget, proved rather than asserted.
/// </summary>
/// <remarks>
/// The action-payload ceiling bounds canonical JSON bytes, not the text a patch is written in, and
/// the difference is a factor of six rather than a rounding error. Reading the ceiling as a raw
/// budget would admit patches that cannot be carried, and the failure would appear late, at proposal
/// time, on a change that had already been generated and applied. These tests take the worst input
/// the derivation claims to cover, carry it through the same canonical writer the approval contract
/// uses, and compare the result against the same constant the validator compares against.
/// </remarks>
public sealed class SourcePatchLimitsTests
{
    [Fact]
    public void RawBudget_IsTheCeilingDividedByTheWorstCaseExpansion()
    {
        // 65536 canonical bytes, less 1024 reserved for the fields that travel beside the patch and
        // the two quotes around the patch itself, divided by six.
        Assert.Equal(65536, ActionApprovalLimits.MaximumPayloadBytes);
        Assert.Equal(10751, SourcePatchLimits.RawBudgetBytes);
        Assert.Equal(
            SourcePatchLimits.RawBudgetBytes,
            SourcePatchLimits.For(new SourceContextOptions(), SourceWorkspaceBounds.Default).MaximumPatchBytes);
    }

    [Fact]
    public void For_TakesTheSizeExtensionAndDepthBoundsFromTheOptionsInForce()
    {
        // The bound a patch is checked against and the bound the excerpt reader applies are the same
        // number because they are read from the same place. Restating either here would let a
        // lowered configuration leave a patch able to write a file the evidence path then refuses to
        // open, which is the one thing the size bound exists to prevent.
        var options = new SourceContextOptions { MaxSourceBytes = 4096, AllowedExtensions = [".cs", ".csproj"] };
        var bounds = SourceWorkspaceBounds.Default with { MaxDepth = 6 };

        var limits = SourcePatchLimits.For(options, bounds);

        Assert.Equal(4096, limits.MaximumTargetBytes);
        Assert.Equal([".cs", ".csproj"], limits.AllowedExtensions);
        Assert.Equal(6, limits.MaximumPathSegments);
    }

    [Theory]
    // The worst ASCII case: the canonical writer escapes these to a six-byte \uXXXX form, and C#
    // source is full of them.
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('&')]
    [InlineData('\'')]
    [InlineData('+')]
    // A control character, escaped the same way.
    [InlineData((char)1)]
    public void RawBudget_LeavesAWorstCasePatchInsideTheActionPayloadCeiling(char worst)
    {
        var canonical = CanonicalPayload(new string(worst, SourcePatchLimits.RawBudgetBytes));

        Assert.True(
            Encoding.UTF8.GetByteCount(canonical) <= ActionApprovalLimits.MaximumPayloadBytes,
            "A patch at the raw budget must fit the canonical payload ceiling after escaping.");
    }

    [Theory]
    // Two bytes of UTF-8, six of canonical JSON: three per byte, under the factor of six.
    [InlineData('é')]
    // Three bytes of UTF-8, six of canonical JSON.
    [InlineData('€')]
    public void RawBudget_LeavesANonAsciiPatchInsideTheCeilingToo(char worst)
    {
        var characters = SourcePatchLimits.RawBudgetBytes / Encoding.UTF8.GetByteCount(worst.ToString());
        var canonical = CanonicalPayload(new string(worst, characters));

        Assert.True(
            Encoding.UTF8.GetByteCount(canonical) <= ActionApprovalLimits.MaximumPayloadBytes,
            "A patch at the raw budget must fit the canonical payload ceiling after escaping.");
    }

    [Fact]
    public void RawBudget_WouldNotFitIfTheCeilingWereReadAsARawBudget() =>
        // The mistake this constant exists to prevent, made on purpose so its size is visible.
        Assert.True(
            Encoding.UTF8.GetByteCount(CanonicalPayload(new string('<', ActionApprovalLimits.MaximumPayloadBytes))) >
            ActionApprovalLimits.MaximumPayloadBytes);

    /// <summary>
    /// The envelope the reserve is sized for: the patch, the base identity it applies to and the
    /// result identity it produced, canonicalized exactly as an action payload is.
    /// </summary>
    private static string CanonicalPayload(string patch) =>
        CanonicalJsonSerializer.Canonicalize(new JsonObject
        {
            ["baseTreeIdentity"] = new string('a', 64),
            ["patch"] = patch,
            ["resultTreeIdentity"] = new string('b', 64),
        });
}
