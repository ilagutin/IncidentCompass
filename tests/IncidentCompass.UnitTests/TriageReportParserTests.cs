using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;

namespace IncidentCompass.UnitTests;

public sealed class TriageReportParserTests
{
    [Fact]
    public void Parse_CompletedWithoutEvidence_ThrowsValidationException()
    {
        using var arguments = JsonDocument.Parse("""
            {
              "report_json": {
                "status": "Completed",
                "summary": "No evidence report.",
                "classification": "SimpleKnownError",
                "confidence": "Medium",
                "documentationFit": "Current",
                "evidence": [],
                "limitations": [],
                "recommendedNextAction": "Review."
              }
            }
            """);

        var exception = Assert.Throws<TriageReportValidationException>(() => TriageReportParser.Parse(arguments.RootElement));

        Assert.Contains("at least one evidence", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_InsufficientEvidenceWithoutEvidence_ReturnsReport()
    {
        using var arguments = JsonDocument.Parse("""
            {
              "report_json": {
                "status": "InsufficientEvidence",
                "summary": "Not enough grounded context.",
                "classification": "Unknown",
                "confidence": "Low",
                "documentationFit": "Missing",
                "evidence": [],
                "limitations": ["No matching memory."],
                "recommendedNextAction": "Collect more context."
              }
            }
            """);

        var report = TriageReportParser.Parse(arguments.RootElement);

        Assert.Equal(TriageReportStatus.InsufficientEvidence, report.Status);
        Assert.Equal(DocumentationFitStatus.Missing, report.DocumentationFit);
        Assert.Empty(report.Evidence);
    }

    [Theory]
    [InlineData("Current", DocumentationFitStatus.Current)]
    [InlineData("CurrentWithHistorical", DocumentationFitStatus.CurrentWithHistorical)]
    [InlineData("StaleOnly", DocumentationFitStatus.StaleOnly)]
    [InlineData("Missing", DocumentationFitStatus.Missing)]
    [InlineData("MultipleCurrentDocuments", DocumentationFitStatus.MultipleCurrentDocuments)]
    public void Parse_DocumentationFitStatus_ReturnsTypedValue(string documentationFit, DocumentationFitStatus expected)
    {
        using var arguments = JsonDocument.Parse($$"""
            {
              "report_json": {
                "status": "Completed",
                "summary": "Documentation assessment.",
                "classification": "SimpleKnownError",
                "confidence": "Medium",
                "documentationFit": "{{documentationFit}}",
                "evidence": [{ "referenceId": "artifact:00000000-0000-0000-0000-000000000001" }],
                "limitations": [],
                "recommendedNextAction": "Review."
              }
            }
            """);

        var report = TriageReportParser.Parse(arguments.RootElement);

        Assert.Equal(expected, report.DocumentationFit);
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("current")]
    public void Parse_InvalidDocumentationFit_ThrowsValidationException(string documentationFit)
    {
        using var arguments = JsonDocument.Parse($$"""
            {
              "report_json": {
                "status": "Completed",
                "summary": "Documentation assessment.",
                "classification": "SimpleKnownError",
                "confidence": "Medium",
                "documentationFit": "{{documentationFit}}",
                "evidence": [{ "referenceId": "artifact:00000000-0000-0000-0000-000000000001" }],
                "limitations": [],
                "recommendedNextAction": "Review."
              }
            }
            """);

        var exception = Assert.Throws<TriageReportValidationException>(() => TriageReportParser.Parse(arguments.RootElement));

        Assert.Contains("documentationFit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MissingDocumentationFit_ThrowsValidationException()
    {
        using var arguments = JsonDocument.Parse("""
            {
              "report_json": {
                "status": "Completed",
                "summary": "Documentation assessment.",
                "classification": "SimpleKnownError",
                "confidence": "Medium",
                "evidence": [{ "referenceId": "artifact:00000000-0000-0000-0000-000000000001" }],
                "limitations": [],
                "recommendedNextAction": "Review."
              }
            }
            """);

        var exception = Assert.Throws<TriageReportValidationException>(() => TriageReportParser.Parse(arguments.RootElement));

        Assert.Contains("documentationFit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NumericStatusString_ThrowsValidationException()
    {
        using var arguments = JsonDocument.Parse("""
            {
              "report_json": {
                "status": "1",
                "summary": "Numeric status.",
                "classification": "SimpleKnownError",
                "confidence": "Medium",
                "documentationFit": "Current",
                "evidence": [{ "referenceId": "artifact:00000000-0000-0000-0000-000000000001" }],
                "limitations": [],
                "recommendedNextAction": "Review."
              }
            }
            """);

        Assert.Throws<TriageReportValidationException>(() => TriageReportParser.Parse(arguments.RootElement));
    }

    [Theory]
    [InlineData("report_json")]
    [InlineData("report")]
    [InlineData("bare")]
    public void Parse_EachSingleEnvelopeShape_ReturnsTheReport(string shape)
    {
        var arguments = shape == "bare" ? ValidReport() : new JsonObject { [shape] = ValidReport() };

        var report = Parse(arguments);

        Assert.Equal("Envelope report.", report.Summary);
    }

    public static TheoryData<string, string> AmbiguousEnvelopes => new()
    {
        { """{"report_json":{},"report":{}}""", OrchestratorRepromptDiagnostics.ReportEnvelopeHasBothWrappers },
        { """{"report_json":{"status":"Completed"},"report":null}""", OrchestratorRepromptDiagnostics.ReportEnvelopeHasBothWrappers },
        { """{"report":{"status":"Completed"},"report_json":null}""", OrchestratorRepromptDiagnostics.ReportEnvelopeHasBothWrappers },
        { """{"report":{},"report_json":{}}""", OrchestratorRepromptDiagnostics.ReportEnvelopeHasBothWrappers },
        { """{"status":"Completed","report_json":{},"report":{}}""", OrchestratorRepromptDiagnostics.ReportEnvelopeHasBothWrappers },
        { """{"report_json":{},"summary":"Beside the wrapper."}""", OrchestratorRepromptDiagnostics.ReportJsonWrapperNotAlone },
        { """{"report_json":{},"report_json":{}}""", OrchestratorRepromptDiagnostics.ReportJsonWrapperNotAlone },
        { """{"report":{},"summary":"Beside the wrapper."}""", OrchestratorRepromptDiagnostics.ReportWrapperNotAlone },
        { """{"report":{},"report":{}}""", OrchestratorRepromptDiagnostics.ReportWrapperNotAlone }
    };

    [Theory]
    [MemberData(nameof(AmbiguousEnvelopes))]
    public void Parse_AmbiguousEnvelope_IsRefusedWithANamedDiagnostic(string argumentsJson, string expectedMessage)
    {
        using var arguments = JsonDocument.Parse(argumentsJson);

        var exception = Assert.Throws<TriageReportValidationException>(() => TriageReportParser.Parse(arguments.RootElement));

        Assert.Equal(expectedMessage, exception.Message);
    }

    /// <summary>
    /// A bare report that also carries a wrapper holding a complete report is the case precedence
    /// used to resolve silently in favour of the wrapper. It is refused whichever wrapper it carries.
    /// </summary>
    [Theory]
    [InlineData("report_json", OrchestratorRepromptDiagnostics.ReportJsonWrapperNotAlone)]
    [InlineData("report", OrchestratorRepromptDiagnostics.ReportWrapperNotAlone)]
    public void Parse_BareReportCarryingAWrapper_IsRefused(string wrapper, string expectedMessage)
    {
        var arguments = ValidReport();
        arguments[wrapper] = ValidReport();

        var exception = Assert.Throws<TriageReportValidationException>(() => Parse(arguments));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Theory]
    [InlineData("summary", TriageReportParser.MaxSummaryLength, OrchestratorRepromptDiagnostics.SummaryTooLong)]
    [InlineData("recommendedNextAction", TriageReportParser.MaxRecommendedNextActionLength, OrchestratorRepromptDiagnostics.RecommendedNextActionTooLong)]
    public void Parse_ReportStringBound_AcceptsTheLimitAndRefusesOneOver(string propertyName, int limit, string expectedMessage)
    {
        var atLimit = ValidReport();
        atLimit[propertyName] = new string('a', limit);
        var overLimit = ValidReport();
        overLimit[propertyName] = new string('a', limit + 1);

        Parse(atLimit);
        var exception = Assert.Throws<TriageReportValidationException>(() => Parse(overLimit));

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public void Parse_LimitationsBounds_AcceptTheLimitsAndRefuseOneOver()
    {
        var atLimit = ValidReport();
        atLimit["limitations"] = Limitations(TriageReportParser.MaxLimitationItems, TriageReportParser.MaxLimitationLength);
        var tooMany = ValidReport();
        tooMany["limitations"] = Limitations(TriageReportParser.MaxLimitationItems + 1, 1);
        var tooLong = ValidReport();
        tooLong["limitations"] = Limitations(1, TriageReportParser.MaxLimitationLength + 1);

        Assert.Equal(TriageReportParser.MaxLimitationItems, Parse(atLimit).Limitations.Count);
        Assert.Equal(
            OrchestratorRepromptDiagnostics.TooManyLimitations,
            Assert.Throws<TriageReportValidationException>(() => Parse(tooMany)).Message);
        Assert.Equal(
            OrchestratorRepromptDiagnostics.LimitationTooLong,
            Assert.Throws<TriageReportValidationException>(() => Parse(tooLong)).Message);
    }

    [Fact]
    public void Parse_EvidenceBounds_AcceptTheLimitsAndRefuseOneOver()
    {
        var atLimit = ValidReport();
        atLimit["evidence"] = Evidence(TriageReportParser.MaxEvidenceItems, TriageReportParser.MaxQuoteLength);
        var tooMany = ValidReport();
        tooMany["evidence"] = Evidence(TriageReportParser.MaxEvidenceItems + 1, quoteLength: null);
        var quoteTooLong = ValidReport();
        quoteTooLong["evidence"] = Evidence(1, TriageReportParser.MaxQuoteLength + 1);

        Assert.Equal(TriageReportParser.MaxEvidenceItems, Parse(atLimit).Evidence.Count);
        Assert.Equal(
            OrchestratorRepromptDiagnostics.TooManyEvidenceItems,
            Assert.Throws<TriageReportValidationException>(() => Parse(tooMany)).Message);
        Assert.Equal(
            OrchestratorRepromptDiagnostics.QuoteTooLong,
            Assert.Throws<TriageReportValidationException>(() => Parse(quoteTooLong)).Message);
    }

    /// <summary>
    /// Bounds count code points, as JSON Schema <c>maxLength</c> does, so a summary of astral
    /// characters at the advertised limit is not refused for being twice as long in UTF-16 units.
    /// </summary>
    [Fact]
    public void Parse_SummaryBound_CountsCodePointsNotUtf16Units()
    {
        const string astral = "\U0001F525";
        var atLimit = ValidReport();
        atLimit["summary"] = string.Concat(Enumerable.Repeat(astral, TriageReportParser.MaxSummaryLength));
        var overLimit = ValidReport();
        overLimit["summary"] = string.Concat(Enumerable.Repeat(astral, TriageReportParser.MaxSummaryLength + 1));

        Parse(atLimit);
        Assert.Equal(
            OrchestratorRepromptDiagnostics.SummaryTooLong,
            Assert.Throws<TriageReportValidationException>(() => Parse(overLimit)).Message);
    }

    private static TriageReport Parse(JsonObject arguments)
    {
        using var document = JsonDocument.Parse(arguments.ToJsonString());
        return TriageReportParser.Parse(document.RootElement);
    }

    private static JsonObject ValidReport() => new()
    {
        ["status"] = "Completed",
        ["summary"] = "Envelope report.",
        ["classification"] = "SimpleKnownError",
        ["confidence"] = "Medium",
        ["documentationFit"] = "Current",
        ["evidence"] = Evidence(1, quoteLength: null),
        ["limitations"] = new JsonArray(),
        ["recommendedNextAction"] = "Review."
    };

    private static JsonArray Limitations(int count, int length) =>
        new(Enumerable.Range(0, count).Select(_ => (JsonNode?)JsonValue.Create(new string('l', length))).ToArray());

    private static JsonArray Evidence(int count, int? quoteLength) =>
        new(Enumerable.Range(0, count).Select(index =>
        {
            var item = new JsonObject { ["referenceId"] = $"artifact:00000000-0000-0000-0000-{index + 1:D12}" };
            if (quoteLength is not null)
            {
                item["quote"] = new string('q', quoteLength.Value);
            }

            return (JsonNode?)item;
        }).ToArray());
}
