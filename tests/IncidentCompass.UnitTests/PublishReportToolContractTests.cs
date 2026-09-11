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

    private static JsonElement ToolSchema(string toolName)
    {
        var definition = Assert.Single(
            OrchestratorToolDefinitions.Create(TestTriageConfiguration.Create()),
            (AiToolDefinition tool) => string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        return definition.InputSchema;
    }
}
