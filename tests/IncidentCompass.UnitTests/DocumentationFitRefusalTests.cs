using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Infrastructure.Investigation;

namespace IncidentCompass.UnitTests;

/// <summary>
/// A <c>documentationFit</c> refusal names the value the backend derived, which is the one piece of
/// backend state a correction turn needs in order to be a correction rather than another guess.
/// These tests are the standing argument that naming it discloses nothing else: the message is a
/// pure function of a five-value enum the backend already publishes in the <c>publish_report</c>
/// tool schema, so no title, quote, artifact id, release marker or document text can ride along.
/// </summary>
public sealed class DocumentationFitRefusalTests
{
    private const string SecretishTitle = "MUST_NOT_ESCAPE_TITLE";
    private const string SecretishQuote = "MUST_NOT_ESCAPE_QUOTE";

    [Fact]
    public void EveryMismatchMessage_NamesItsDerivedValueAndDiffersOnlyByThatName()
    {
        var values = Enum.GetValues<DocumentationFitStatus>();
        var templates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var message = DocumentationFitDiagnostics.Mismatch(value);

            Assert.Contains(value.ToString(), message, StringComparison.Ordinal);
            // Blanking the one derived name must collapse every message to the same sentence. If any
            // other part of a message varied - anything drawn from a document or a model - the set
            // below would hold more than one template.
            templates.Add(message.Replace(value.ToString(), "<derived>", StringComparison.Ordinal));
        }

        Assert.Single(templates);
        Assert.Equal(values.Length, DocumentationFitDiagnostics.AllMismatchMessages().Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The processor forwards a report-validation message to the model only when it matches the
    /// closed diagnostic allowlist exactly; anything else collapses to a content-free fallback.
    /// Every message the mismatch family can produce must pass that gate, or naming the derived
    /// value would silently stop reaching the model.
    /// </summary>
    [Fact]
    public void EveryMismatchMessage_SurvivesTheClosedDiagnosticAllowlistVerbatim()
    {
        foreach (var message in DocumentationFitDiagnostics.AllMismatchMessages())
        {
            Assert.Equal(
                message,
                OrchestratorRepromptDiagnostics.ForReportValidation(new TriageReportValidationException(message)));
        }

        // The gate is still closed: a near-miss of the same family does not pass.
        Assert.Equal(
            OrchestratorRepromptDiagnostics.UnknownReportValidationFailure,
            OrchestratorRepromptDiagnostics.ForReportValidation(new TriageReportValidationException(
                DocumentationFitDiagnostics.Mismatch(DocumentationFitStatus.StaleOnly) + " " + SecretishQuote)));
    }

    /// <summary>
    /// End to end through the resolver that actually refuses: the message it throws is exactly the
    /// message for the value it derived, and carries none of the evidence it derived it from, even
    /// though that evidence is right there in the same call.
    /// </summary>
    [Theory]
    [InlineData(new[] { "Stale", "Stale" }, DocumentationFitStatus.StaleOnly)]
    [InlineData(new[] { "Current" }, DocumentationFitStatus.Current)]
    [InlineData(new[] { "Current", "Stale" }, DocumentationFitStatus.CurrentWithHistorical)]
    [InlineData(new[] { "Current", "Current" }, DocumentationFitStatus.MultipleCurrentDocuments)]
    [InlineData(new[] { "Unversioned", "ServiceMismatch" }, DocumentationFitStatus.Missing)]
    public void ResolverRefusal_NamesTheDerivedValueAndNothingFromTheDocuments(
        string[] documentationStatuses,
        DocumentationFitStatus expected)
    {
        var wrongValue = Enum.GetValues<DocumentationFitStatus>().First(value => value != expected);
        var evidence = documentationStatuses
            .Select(static (status, index) => new GroundedReportEvidence(
                Guid.NewGuid(),
                "RetrievedItem",
                "artifact:" + Guid.NewGuid(),
                SecretishQuote + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                0.9,
                Guid.NewGuid(),
                status))
            .ToArray();

        var exception = Assert.Throws<TriageReportValidationException>(() =>
            new PostgresDocumentationFitResolver().ValidateAndApply(Report(wrongValue), evidence));

        Assert.Equal(DocumentationFitDiagnostics.Mismatch(expected), exception.Message);
        Assert.DoesNotContain(SecretishQuote, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretishTitle, exception.Message, StringComparison.Ordinal);
        foreach (var item in evidence)
        {
            Assert.DoesNotContain(item.ArtifactId.ToString(), exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(item.Reference, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ResolverAccepts_TheValueTheSharedCalculatorDerives()
    {
        string?[] statuses = ["Current", "Stale"];
        var derived = DocumentationFitCalculator.Resolve(statuses);
        var evidence = statuses
            .Select(static status => new GroundedReportEvidence(
                Guid.NewGuid(), "RetrievedItem", "artifact:x", null, null, Guid.NewGuid(), status))
            .ToArray();

        var applied = new PostgresDocumentationFitResolver().ValidateAndApply(Report(derived), evidence);

        Assert.Equal(DocumentationFitStatus.CurrentWithHistorical, derived);
        Assert.Equal(derived, applied.DocumentationFit);
    }

    private static TriageReport Report(DocumentationFitStatus documentationFit) =>
        new(
            TriageReportStatus.Completed,
            SecretishTitle,
            "KnownIncident",
            "Medium",
            [new TriageReportEvidenceReference("artifact:00000000-0000-0000-0000-000000000001", SecretishQuote)],
            [],
            "Follow the cited runbook.")
        {
            DocumentationFit = documentationFit
        };
}
