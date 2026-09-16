using System.Globalization;
using System.Text.Json;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The orchestrator is handed a tool schema and then judged by a parser. These tests prove the two
/// describe the same object, rather than trusting that whoever last edited one remembered the other.
/// <para>
/// The failure this exists to catch is silent and expensive: <c>report_json</c> declares
/// <c>additionalProperties: true</c>, so a field the parser demands but the schema never mentions is
/// not rejected at the tool boundary - it simply never appears in what a well-behaved model sends,
/// and every first publish is refused for a field the model was never told about.
/// </para>
/// </summary>
public sealed class PublishReportToolContractTests
{
    /// <summary>
    /// Membership is measured, not listed: a property is "required by the parser" when deleting it
    /// from an otherwise valid report makes the parser refuse. The set measured that way must equal
    /// the schema's own <c>required</c> array, and equal the set of properties the schema declares at
    /// all, so neither side can carry a field the other does not.
    /// </summary>
    [Fact]
    public void PublishReportSchemaRequiredSet_EqualsTheSetTheParserRefusesWithout()
    {
        var reportSchema = PublishReportSchema().GetProperty("properties").GetProperty("report_json");
        var declared = reportSchema.GetProperty("properties")
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var required = reportSchema.GetProperty("required")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        var refusedWithout = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propertyName in declared)
        {
            var withoutProperty = ValidReport();
            withoutProperty.Remove(propertyName);
            if (Record.Exception(() => ParseReport(withoutProperty)) is TriageReportValidationException)
            {
                refusedWithout.Add(propertyName);
            }
        }

        // The base payload must itself be valid, or every deletion would "fail" for the wrong reason.
        Assert.NotNull(ParseReport(ValidReport()));
        Assert.Equal(declared.OrderBy(static name => name, StringComparer.Ordinal), refusedWithout.OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal(declared.OrderBy(static name => name, StringComparer.Ordinal), required.OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Contains("documentationFit", required);
    }

    /// <summary>
    /// Every value the schema offers for an enumerated field must be a value the parser accepts.
    /// A schema that advertises a value the validator refuses is the same defect in the other
    /// direction: the model does as it was told and is refused for it.
    /// </summary>
    [Theory]
    [InlineData("documentationFit")]
    [InlineData("confidence")]
    [InlineData("classification")]
    [InlineData("status")]
    public void EveryEnumeratedValueThePublishReportSchemaOffers_ParsesInAReportThatUsesIt(string propertyName)
    {
        var values = EnumeratedValues(propertyName);

        Assert.NotEmpty(values);
        foreach (var value in values)
        {
            var report = ValidReport();
            report[propertyName] = value;
            ApplyStatusCoupling(report);

            var parsed = ParseReport(report);

            Assert.Equal(value, ReadBack(parsed, propertyName));
        }
    }

    /// <summary>
    /// And the reverse for <c>documentationFit</c>, which is the field the backend refuses on an
    /// exact match: the schema's enumeration is generated from the same enum the parser parses into,
    /// so the two lists cannot drift even by one name.
    /// </summary>
    [Fact]
    public void DocumentationFitEnumeration_IsExactlyTheStatusEnumTheParserProduces()
    {
        Assert.Equal(Enum.GetNames<DocumentationFitStatus>(), EnumeratedValues("documentationFit"));
    }

    [Fact]
    public void ClassificationEnumeration_IsExactlyTheVocabularyTheParserAccepts()
    {
        Assert.All(
            EnumeratedValues("classification"),
            value => Assert.True(TriageClassificationVocabulary.Contains(value)));
    }

    /// <summary>
    /// The other tool the orchestrator is given, checked the same way. It is here so that "the
    /// schema and the validator agree" is a property of the orchestrator's whole tool surface and
    /// not of the one tool that was known to be broken.
    /// </summary>
    [Fact]
    public void DelegateSchemaRequiredSet_EqualsTheSetTheExecutorRefusesWithout()
    {
        var schema = ToolSchema(OrchestratorToolNames.Delegate);
        var declared = schema.GetProperty("properties")
            .EnumerateObject()
            .Select(static property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var required = schema.GetProperty("required")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            declared.OrderBy(static name => name, StringComparer.Ordinal),
            required.OrderBy(static name => name, StringComparer.Ordinal));
        Assert.Equal(["role", "task"], required.OrderBy(static name => name, StringComparer.Ordinal));
    }

    /// <summary>
    /// Each row is one report bound: the bounded value, the phrase the tool description must carry
    /// (a composite format filled with the parser constant), the parser constant and its refusal.
    /// </summary>
    public static TheoryData<string, string, int, string> ReportBounds => new()
    {
        { "summary", "summary at most {0} characters", TriageReportParser.MaxSummaryLength, OrchestratorRepromptDiagnostics.SummaryTooLong },
        { "recommendedNextAction", "recommendedNextAction at most {0}", TriageReportParser.MaxRecommendedNextActionLength, OrchestratorRepromptDiagnostics.RecommendedNextActionTooLong },
        { "limitations", "limitations at most {0} items", TriageReportParser.MaxLimitationItems, OrchestratorRepromptDiagnostics.TooManyLimitations },
        { "limitations.item", "items of at most {0} characters", TriageReportParser.MaxLimitationLength, OrchestratorRepromptDiagnostics.LimitationTooLong },
        { "evidence", "evidence at most {0} items", TriageReportParser.MaxEvidenceItems, OrchestratorRepromptDiagnostics.TooManyEvidenceItems },
        { "evidence.quote", "quote at most {0} characters", TriageReportParser.MaxQuoteLength, OrchestratorRepromptDiagnostics.QuoteTooLong }
    };

    /// <summary>
    /// The model and the backend must agree on every bound: the tool description states exactly the
    /// parser constant, the refusal the model is reprompted with names that same limit, and the
    /// refusal is on the reprompt allowlist so it reaches the model verbatim instead of the fallback.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReportBounds))]
    public void ReportBound_IsStatedInTheDescriptionAndItsRefusalNamesItOnTheAllowlist(
        string boundedValue,
        string descriptionPhrase,
        int parserLimit,
        string refusal)
    {
        _ = boundedValue;
        var limit = parserLimit.ToString(CultureInfo.InvariantCulture);

        Assert.Contains(
            string.Format(CultureInfo.InvariantCulture, descriptionPhrase, parserLimit),
            PublishReportTool().Description,
            StringComparison.Ordinal);
        Assert.Contains(" " + limit + " ", refusal, StringComparison.Ordinal);
        Assert.Equal(refusal, OrchestratorRepromptDiagnostics.ForReportValidation(new TriageReportValidationException(refusal)));
    }

    /// <summary>
    /// The stated numbers are also measured against the parser's behaviour, not only its constants:
    /// a value exactly at each bound parses, and one past it is refused with the named diagnostic.
    /// </summary>
    [Theory]
    [MemberData(nameof(ReportBounds))]
    public void ReportBound_IsTheBoundaryTheParserEnforces(
        string boundedValue,
        string descriptionPhrase,
        int parserLimit,
        string refusal)
    {
        _ = descriptionPhrase;

        Assert.NotNull(ParseReport(ReportWithBoundedValue(boundedValue, parserLimit)));
        var exception = Assert.Throws<TriageReportValidationException>(
            () => ParseReport(ReportWithBoundedValue(boundedValue, parserLimit + 1)));
        Assert.Equal(refusal, exception.Message);
    }

    /// <summary>
    /// The bounds are deliberately not schema keywords: grammar-constrained local runtimes expand
    /// <c>maxLength</c> and <c>maxItems</c> into large repetition rules, so neither may appear anywhere
    /// in the publish_report schema.
    /// </summary>
    [Fact]
    public void PublishReportSchema_CarriesNoLengthOrCountKeywords()
    {
        var schemaText = PublishReportSchema().GetRawText();

        Assert.DoesNotContain("maxLength", schemaText, StringComparison.Ordinal);
        Assert.DoesNotContain("maxItems", schemaText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OrchestratorRepromptDiagnostics.ReportEnvelopeHasBothWrappers)]
    [InlineData(OrchestratorRepromptDiagnostics.ReportJsonWrapperNotAlone)]
    [InlineData(OrchestratorRepromptDiagnostics.ReportWrapperNotAlone)]
    public void EnvelopeRefusal_IsOnTheRepromptAllowlist(string refusal)
    {
        Assert.Equal(refusal, OrchestratorRepromptDiagnostics.ForReportValidation(new TriageReportValidationException(refusal)));
    }

    private static Dictionary<string, object> ReportWithBoundedValue(string boundedValue, int size)
    {
        var report = ValidReport();
        switch (boundedValue)
        {
            case "summary":
            case "recommendedNextAction":
                report[boundedValue] = new string('a', size);
                break;
            case "limitations":
                report[boundedValue] = Enumerable.Repeat("Limitation.", size).ToArray();
                break;
            case "limitations.item":
                report["limitations"] = new[] { new string('l', size) };
                break;
            case "evidence":
                report[boundedValue] = Enumerable.Range(0, size).Select(static _ => EvidenceItem(null)).ToArray();
                break;
            case "evidence.quote":
                report["evidence"] = new[] { EvidenceItem(new string('q', size)) };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(boundedValue), boundedValue, "Unhandled bound.");
        }

        return report;
    }

    private static Dictionary<string, object> EvidenceItem(string? quote)
    {
        var item = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["referenceId"] = "artifact:00000000-0000-0000-0000-000000000001"
        };
        if (quote is not null)
        {
            item["quote"] = quote;
        }

        return item;
    }

    private static string ReadBack(TriageReport report, string propertyName) => propertyName switch
    {
        "documentationFit" => report.DocumentationFit.ToString(),
        "confidence" => report.Confidence,
        "classification" => report.Classification,
        "status" => report.Status.ToString(),
        _ => throw new ArgumentOutOfRangeException(nameof(propertyName), propertyName, "Unhandled property.")
    };

    /// <summary>
    /// <c>status</c> is not independent of the rest of the report: an InsufficientEvidence report
    /// must classify as Unknown and a Completed one must not, and a Completed report must cite
    /// evidence. The sweep applies those coupled rules so that a value is tested for its own sake
    /// rather than failing on an unrelated one.
    /// </summary>
    private static void ApplyStatusCoupling(Dictionary<string, object> report)
    {
        if (string.Equals(report["status"] as string, "InsufficientEvidence", StringComparison.Ordinal))
        {
            report["classification"] = TriageClassificationVocabulary.Unknown;
        }
        else if (string.Equals(report["classification"] as string, TriageClassificationVocabulary.Unknown, StringComparison.Ordinal))
        {
            report["status"] = "InsufficientEvidence";
        }
    }

    private static string[] EnumeratedValues(string propertyName) =>
        PublishReportSchema()
            .GetProperty("properties")
            .GetProperty("report_json")
            .GetProperty("properties")
            .GetProperty(propertyName)
            .GetProperty("enum")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToArray();

    private static Dictionary<string, object> ValidReport() => new(StringComparer.Ordinal)
    {
        ["status"] = "Completed",
        ["summary"] = "Checkout requests are timing out.",
        ["classification"] = "KnownIncident",
        ["confidence"] = "Medium",
        ["documentationFit"] = "Missing",
        ["evidence"] = new[]
        {
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["referenceId"] = "artifact:00000000-0000-0000-0000-000000000001"
            }
        },
        ["limitations"] = Array.Empty<string>(),
        ["recommendedNextAction"] = "Follow the cited runbook."
    };

    private static TriageReport ParseReport(Dictionary<string, object> report)
    {
        using var document = JsonSerializer.SerializeToDocument(
            new Dictionary<string, object>(StringComparer.Ordinal) { ["report_json"] = report });
        return TriageReportParser.Parse(document.RootElement);
    }

    private static JsonElement PublishReportSchema() => ToolSchema(OrchestratorToolNames.PublishReport);

    private static AiToolDefinition PublishReportTool() => ToolDefinition(OrchestratorToolNames.PublishReport);

    private static JsonElement ToolSchema(string toolName) => ToolDefinition(toolName).InputSchema;

    private static AiToolDefinition ToolDefinition(string toolName) =>
        Assert.Single(
            OrchestratorToolDefinitions.Create(TestTriageConfiguration.Create()),
            (AiToolDefinition tool) => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
}
