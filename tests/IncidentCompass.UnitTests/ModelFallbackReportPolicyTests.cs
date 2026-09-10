using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.Fallback;

namespace IncidentCompass.UnitTests;

/// <summary>
/// A report produced with help from a fallback route says so. The sentence is backend-owned in both
/// directions, exactly like the withheld-evidence marker: the model cannot suppress it and cannot
/// claim it.
/// </summary>
public sealed class ModelFallbackReportPolicyTests
{
    [Fact]
    public void Apply_StatesTheDegradedRun_WhenACallWasAnsweredByAFallback()
    {
        var result = ModelFallbackReportPolicy.Apply(
            Report(),
            [new AttemptModelFallback("report-chat", "backup-chat")]);

        Assert.Equal(
            ["original", ModelFallbackReportPolicy.DegradedRoutingLimitation],
            result.Limitations);
    }

    [Fact]
    public void Apply_StatesTheDegradedRunOnce_WhenSeveralRoutesFailedOver()
    {
        var result = ModelFallbackReportPolicy.Apply(
            Report(),
            [
                new AttemptModelFallback("report-chat", "backup-chat"),
                new AttemptModelFallback("analysis-chat", "backup-chat")
            ]);

        Assert.Equal(
            ["original", ModelFallbackReportPolicy.DegradedRoutingLimitation],
            result.Limitations);
    }

    [Fact]
    public void Apply_LeavesTheReportAlone_WhenNothingFailedOver()
    {
        var result = ModelFallbackReportPolicy.Apply(Report(), []);

        Assert.Equal(["original"], result.Limitations);
    }

    /// <summary>
    /// The model authors its own limitations, so it can write the reserved sentence into them and
    /// claim a degraded run that never happened. Where the backend derived no fail-over, the copy is
    /// removed.
    /// </summary>
    [Fact]
    public void Apply_RemovesAModelAuthoredClaim_WhenNothingFailedOver()
    {
        var report = Report() with
        {
            Limitations = ["original", ModelFallbackReportPolicy.DegradedRoutingLimitation]
        };

        var result = ModelFallbackReportPolicy.Apply(report, []);

        Assert.Equal(["original"], result.Limitations);
    }

    [Fact]
    public void Apply_StatesTheDegradedRunOnce_WhenTheModelAlreadyWroteIt()
    {
        var report = Report() with
        {
            Limitations = [ModelFallbackReportPolicy.DegradedRoutingLimitation, "original"]
        };
        IReadOnlyCollection<AttemptModelFallback> fallbacks =
            [new AttemptModelFallback("report-chat", "backup-chat")];

        var result = ModelFallbackReportPolicy.Apply(
            ModelFallbackReportPolicy.Apply(report, fallbacks),
            fallbacks);

        Assert.Equal(
            ["original", ModelFallbackReportPolicy.DegradedRoutingLimitation],
            result.Limitations);
    }

    /// <summary>
    /// Ownership stops at the reserved sentence, byte for byte, so a model-authored near-copy stays
    /// standing as one of the model's own limitations rather than being deleted on a fuzzy match.
    /// The direction that matters needs no matching at all: where the backend derived a fail-over it
    /// appends the sentence, so no model output can hide one.
    /// </summary>
    [Fact]
    public void Apply_LeavesAModelAuthoredNearCopyStanding()
    {
        const string nearCopy =
            "Note: at least one model call behind this report failed on its configured route.";
        var report = Report() with { Limitations = ["original", nearCopy] };

        var result = ModelFallbackReportPolicy.Apply(report, []);

        Assert.Equal(["original", nearCopy], result.Limitations);
    }

    private static TriageReport Report() => new(
        TriageReportStatus.Completed,
        "summary",
        "Dependency",
        "Medium",
        [new TriageReportEvidenceReference("artifact:a0000000-0000-0000-0000-000000000001", null)],
        ["original"],
        "inspect");
}
