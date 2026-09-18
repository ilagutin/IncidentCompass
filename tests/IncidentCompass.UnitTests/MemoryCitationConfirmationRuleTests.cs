using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Memory;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The band ordering and the scope of the rule that reads it. A <c>low</c> item is admitted context
/// and stays citable; what it may not do is carry a <c>KnownIncident</c> classification on its own,
/// because a ticket and a remediation proposal follow from that classification and the reader of a
/// retrieved item is a model.
/// </summary>
public sealed class MemoryCitationConfirmationRuleTests
{
    [Theory]
    [InlineData("high", true)]
    [InlineData("medium", true)]
    [InlineData("low", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("High", false)]
    [InlineData("confirmed", false)]
    public void ConfirmsMatch_AdmitsTheTwoConfirmingBandsAndNothingElse(string? band, bool expected)
    {
        Assert.Equal(expected, MemoryRetrievalConfidence.ConfirmsMatch(band));
    }

    [Fact]
    public void KnownIncidentCitingOnlyUnconfirmedMemory_IsRefused()
    {
        Assert.True(Refuses(TriageReportStatus.Completed, "KnownIncident", MemoryRetrievalConfidence.Low));
    }

    [Fact]
    public void KnownIncidentCitingOneConfirmedDocumentBesideAnUnconfirmedOne_IsAccepted()
    {
        Assert.False(Refuses(
            TriageReportStatus.Completed,
            "KnownIncident",
            MemoryRetrievalConfidence.Low,
            MemoryRetrievalConfidence.Medium));
        Assert.False(Refuses(
            TriageReportStatus.Completed,
            "KnownIncident",
            MemoryRetrievalConfidence.Low,
            MemoryRetrievalConfidence.High));
    }

    /// <summary>
    /// The scoping that keeps a ticket-search or source-lookup citation publishable. Those payload
    /// shapes are closed and carry no band, so the caller passes no band for them, and the empty set
    /// is the same input a report citing no retrieved document at all produces.
    /// </summary>
    [Fact]
    public void KnownIncidentCitingNoMemoryDocumentAtAll_IsAccepted()
    {
        Assert.False(Refuses(TriageReportStatus.Completed, "KnownIncident"));
    }

    [Theory]
    [InlineData("LikelyRegression")]
    [InlineData("SimpleKnownError")]
    [InlineData("Noise")]
    public void AnotherCompletedClassificationCitingOnlyUnconfirmedMemory_IsAccepted(string classification)
    {
        Assert.False(Refuses(TriageReportStatus.Completed, classification, MemoryRetrievalConfidence.Low));
    }

    [Fact]
    public void AnInsufficientEvidenceReport_IsUntouched()
    {
        Assert.False(Refuses(
            TriageReportStatus.InsufficientEvidence,
            TriageClassificationVocabulary.Unknown,
            MemoryRetrievalConfidence.Low));
    }

    /// <summary>
    /// A payload with no band at all is what a memory artifact written before this release looks
    /// like. It does not confirm. The safe direction is not symmetric here: admitting it would let a
    /// re-triage of an old fault rest a <c>KnownIncident</c> classification, and the ticket and diff
    /// that follow, on a match nothing ever judged, while refusing it costs one correction turn on a
    /// report the backend can still publish under another classification.
    /// </summary>
    [Fact]
    public void KnownIncidentCitingOnlyAMemoryDocumentWithNoBandRecorded_IsRefused()
    {
        Assert.True(Refuses(TriageReportStatus.Completed, "KnownIncident", new string?[] { null }));
        Assert.False(Refuses(
            TriageReportStatus.Completed,
            "KnownIncident",
            null,
            MemoryRetrievalConfidence.High));
    }

    /// <summary>
    /// The refusal reaches the model only if its exact text is on the reprompt allowlist; anything
    /// else collapses to the content-free fallback and the correction turn says nothing.
    /// </summary>
    [Fact]
    public void TheRefusal_SurvivesTheRepromptAllowlistVerbatim()
    {
        var diagnostic = OrchestratorRepromptDiagnostics.ForReportValidation(
            new TriageReportValidationException(MemoryCitationConfirmationRule.UnconfirmedMemoryCitationRefusal));

        Assert.Equal(MemoryCitationConfirmationRule.UnconfirmedMemoryCitationRefusal, diagnostic);
        Assert.NotEqual(OrchestratorRepromptDiagnostics.UnknownReportValidationFailure, diagnostic);
    }

    /// <summary>
    /// And it discloses only the rule. The message is one fixed string, so there is no artifact id,
    /// title, quote or model text it could carry; this states that as a test rather than as a claim.
    /// </summary>
    [Fact]
    public void TheRefusal_NamesTheRuleAndNoRetrievedContent()
    {
        var message = MemoryCitationConfirmationRule.UnconfirmedMemoryCitationRefusal;

        Assert.StartsWith("publish_report classification KnownIncident", message, StringComparison.Ordinal);
        Assert.DoesNotContain("artifact:", message, StringComparison.Ordinal);
        Assert.DoesNotContain(MemoryRetrievalConfidence.Low, message, StringComparison.Ordinal);
        Assert.InRange(message.Length, 1, 1000);
    }

    /// <summary>
    /// The backend-authored no-progress report publishes <c>Unknown</c> with
    /// <c>InsufficientEvidence</c>, so a classification-scoped rule cannot reach it whatever it
    /// cites. <c>MemoryCitationConfirmationPublicationTests</c> proves the same thing through the
    /// real publication transaction.
    /// </summary>
    [Fact]
    public void TheBackendAuthoredNoProgressReport_IsOutOfTheRulesScope()
    {
        foreach (var reason in Enum.GetValues<NoProgressTerminationReason>())
        {
            var report = NoProgressTerminationReport.Create([], reason);

            Assert.False(Refuses(report.Status, report.Classification, MemoryRetrievalConfidence.Low));
        }
    }

    private static bool Refuses(TriageReportStatus status, string classification, params string?[] citedMemoryBands) =>
        MemoryCitationConfirmationRule.RefusesUnconfirmedMemoryCitations(status, classification, citedMemoryBands);
}
