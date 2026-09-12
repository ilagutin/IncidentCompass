using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.Redaction;

namespace IncidentCompass.UnitTests;

public sealed class EvidenceRedactionReportPolicyTests
{
    private static readonly Guid FirstArtifact = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondArtifact = Guid.Parse("a0000000-0000-0000-0000-000000000002");

    [Fact]
    public void Apply_AddsTheMarker_WhenACitedArtifactWasRedacted()
    {
        var result = EvidenceRedactionReportPolicy.Apply(
            Report(),
            [
                new CitedEvidenceRedaction(FirstArtifact, false),
                new CitedEvidenceRedaction(SecondArtifact, true)
            ]);

        Assert.Equal(
            ["original", EvidenceRedactionReportPolicy.WithheldEvidenceLimitation],
            result.Limitations);
    }

    /// <summary>
    /// A recorded outcome of <see langword="false" /> is the backend saying the redactor ran and
    /// removed nothing, and an unrecorded outcome is the backend saying nothing at all. Neither is a
    /// reason to tell a reader that evidence was withheld.
    /// </summary>
    [Fact]
    public void Apply_LeavesTheReportAlone_WhenNoCitedArtifactWasRedacted()
    {
        var result = EvidenceRedactionReportPolicy.Apply(
            Report(),
            [
                new CitedEvidenceRedaction(FirstArtifact, false),
                new CitedEvidenceRedaction(SecondArtifact, null)
            ]);

        Assert.Equal(["original"], result.Limitations);
    }

    [Fact]
    public void Apply_LeavesTheReportAlone_WhenNothingWasCited()
    {
        var result = EvidenceRedactionReportPolicy.Apply(Report(), []);

        Assert.Equal(["original"], result.Limitations);
    }

    /// <summary>
    /// The second half of the spoofing problem. The model authors its own limitations and can write
    /// the reserved sentence into them, which would let it claim a withholding that never happened -
    /// exactly as unverifiable as a model-authored claim that nothing was withheld. The sentence is
    /// backend-owned in both directions, so a copy the model wrote is removed when the backend did
    /// not derive the marker.
    /// </summary>
    [Fact]
    public void Apply_RemovesAModelAuthoredMarker_WhenNoCitedArtifactWasRedacted()
    {
        var report = Report() with
        {
            Limitations = ["original", EvidenceRedactionReportPolicy.WithheldEvidenceLimitation]
        };

        var result = EvidenceRedactionReportPolicy.Apply(
            report,
            [new CitedEvidenceRedaction(FirstArtifact, false)]);

        Assert.Equal(["original"], result.Limitations);
    }

    /// <summary>
    /// The same sentence written by the model where the backend does derive the marker must not be
    /// counted twice, and applying the policy to an already-published shape must not grow the list.
    /// </summary>
    [Fact]
    public void Apply_StatesTheMarkerOnce_WhenTheModelAlreadyWroteIt()
    {
        var report = Report() with
        {
            Limitations = [EvidenceRedactionReportPolicy.WithheldEvidenceLimitation, "original"]
        };

        var first = EvidenceRedactionReportPolicy.Apply(
            report,
            [new CitedEvidenceRedaction(FirstArtifact, true)]);
        var second = EvidenceRedactionReportPolicy.Apply(
            first,
            [new CitedEvidenceRedaction(FirstArtifact, true)]);

        Assert.Equal(
            ["original", EvidenceRedactionReportPolicy.WithheldEvidenceLimitation],
            second.Limitations);
    }

    /// <summary>
    /// What the ownership claim does and does not cover. The policy removes the reserved sentence,
    /// byte for byte, and nothing else: a model-authored near-copy - one extra clause here, a
    /// lowercased word there - is not that sentence and stays in the report as one of the model's own
    /// limitations.
    /// <para>
    /// That is the deliberate choice, asserted so a later loosening is a decision rather than a
    /// slip. The direction that matters is covered without any matching: where the backend does
    /// derive the marker it appends the reserved sentence, so no model output can suppress a real
    /// withholding. Loosening the comparison would only strip near-copies claiming a withholding
    /// that did not happen, and it would buy that by deleting report text on a fuzzy match, which is
    /// the one direction that can destroy a genuine limitation a human needed to read.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(
        "Evidence cited by this report includes at least one value that redaction removed before" +
        " the model saw it, and the connector was also unavailable.")]
    [InlineData(
        "evidence cited by this report includes at least one value that redaction removed before" +
        " the model saw it.")]
    [InlineData(
        "Note: evidence cited by this report includes at least one value that redaction removed" +
        " before the model saw it.")]
    public void Apply_LeavesAModelAuthoredNearCopyStanding_BecauseOwnershipStopsAtTheReservedSentence(
        string nearCopy)
    {
        var report = Report() with { Limitations = ["original", nearCopy] };

        var result = EvidenceRedactionReportPolicy.Apply(
            report,
            [new CitedEvidenceRedaction(FirstArtifact, false)]);

        Assert.Equal(["original", nearCopy], result.Limitations);
        Assert.DoesNotContain(EvidenceRedactionReportPolicy.WithheldEvidenceLimitation, result.Limitations);
    }

    private static TriageReport Report() => new(
        TriageReportStatus.Completed,
        "summary",
        "Dependency",
        "Medium",
        [new TriageReportEvidenceReference("artifact:" + FirstArtifact, null)],
        ["original"],
        "inspect");
}
