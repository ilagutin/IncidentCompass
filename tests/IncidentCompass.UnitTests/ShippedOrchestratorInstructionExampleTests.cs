using System.Text.Json;
using System.Text.RegularExpressions;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Memory;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// <c>ShippedWorkerInstructionExamplesTests</c> validates every role's examples against that role's
/// output schema. The orchestrator is not a role, so its one-shot example had no such check, and it
/// shipped teaching a <c>documentationFit</c> value that was wrong for the corpus it ships with.
/// These tests close that hole: the example is parsed by the real parser, measured against the real
/// tool schema, and its <c>documentationFit</c> is recomputed from the document status the same file
/// shows, from the memory role's own shipped example, and from the shipped sample documents.
/// </summary>
public sealed class ShippedOrchestratorInstructionExampleTests
{
    private static readonly Regex JsonExamplePattern = new(
        "~~~json\\r?\\n(?<example>\\{[\\s\\S]*?\\})\\r?\\n~~~",
        RegexOptions.CultureInvariant);

    private static readonly Regex JsonExampleFenceOpeningPattern = new(
        "^(?:~~~|```)json[ \\t]*\\r?$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// The example must teach exactly the shape the tool schema declares and the parser enforces:
    /// no field the model would be refused for omitting, and no field it was never offered.
    /// </summary>
    [Fact]
    public void ShippedPublishExample_ParsesAndCarriesExactlyTheRequiredFieldSet()
    {
        var example = PublishExample();
        var required = PublishReportRequiredProperties();

        var keys = example.EnumerateObject().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            required.OrderBy(static name => name, StringComparer.Ordinal),
            keys.OrderBy(static name => name, StringComparer.Ordinal));

        using var arguments = JsonSerializer.SerializeToDocument(
            new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["report_json"] = example });
        Assert.NotNull(TriageReportParser.Parse(arguments.RootElement));
    }

    /// <summary>
    /// The example's answer is recomputed from the example's own inputs by the same calculator the
    /// backend refuses with, so the instruction cannot demonstrate an answer the backend would reject.
    /// </summary>
    [Fact]
    public void ShippedPublishExample_DocumentationFitIsWhatTheBackendWouldDeriveFromTheDocumentsItCites()
    {
        var publishExample = PublishExample();
        var citedArtifactIds = publishExample.GetProperty("evidence")
            .EnumerateArray()
            .Select(static item => item.GetProperty("referenceId").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var shownDocuments = MemoryResultExample().GetProperty("items")
            .EnumerateArray()
            .ToDictionary(
                static item => item.GetProperty("artifactId").GetString()!,
                static item => item.TryGetProperty("documentationStatus", out var status) ? status.GetString() : null,
                StringComparer.Ordinal);

        // Every id the example cites is an id the example was shown; a citation of something the
        // instruction never displayed would make the derivation below unfalsifiable.
        Assert.NotEmpty(citedArtifactIds);
        Assert.All(citedArtifactIds, id => Assert.True(
            shownDocuments.ContainsKey(id),
            $"The one-shot report cites '{id}', which the instruction's own delegate-result example " +
            "does not contain, so a reader cannot follow the derivation it is being taught."));

        var derived = DocumentationFitCalculator.Resolve(citedArtifactIds.Select(id => shownDocuments[id]));

        Assert.Equal(derived.ToString(), publishExample.GetProperty("documentationFit").GetString());
    }

    /// <summary>
    /// The orchestrator's picture of a memory delegate result must be the memory role's own shipped
    /// output example, or one of the two files is teaching a shape that never occurs.
    /// </summary>
    [Fact]
    public void ShippedMemoryResultExample_AgreesWithTheMemoryRolesOwnShippedExample()
    {
        var orchestratorItem = MemoryResultExample().GetProperty("items").EnumerateArray().Single();
        var memoryRoleItem = MemoryRoleMatchedExample().GetProperty("items").EnumerateArray().Single();

        Assert.Equal(
            memoryRoleItem.GetProperty("artifactId").GetString(),
            orchestratorItem.GetProperty("artifactId").GetString());
        Assert.Equal(
            memoryRoleItem.GetProperty("documentationStatus").GetString(),
            orchestratorItem.GetProperty("documentationStatus").GetString());
        Assert.Equal(
            memoryRoleItem.GetProperty("quote").GetString(),
            orchestratorItem.GetProperty("quote").GetString());
    }

    /// <summary>
    /// And the status the example shows is the status the backend really assigns to the documents
    /// this repository seeds, under the configuration this repository ships. Both sample documents
    /// are evaluated through <c>MemoryDocumentationStatusEvaluator</c> from their own frontmatter,
    /// so the example's answer is derived from the shipped corpus rather than asserted about it.
    /// </summary>
    [Fact]
    public void ShippedSampleDocuments_DeriveTheStatusAndFitTheShippedExampleTeaches()
    {
        var configuration = ShippedConfiguration();
        var statuses = ShippedSampleDocuments()
            .Select(document => MemoryDocumentationStatusEvaluator
                .Assess(configuration, document.ServiceName!, document)
                .Status
                .ToString())
            .ToArray();

        Assert.NotEmpty(statuses);
        Assert.All(
            statuses,
            status => Assert.Equal(
                MemoryResultExample().GetProperty("items")[0].GetProperty("documentationStatus").GetString(),
                status));
        Assert.Equal(
            DocumentationFitCalculator.Resolve(statuses).ToString(),
            PublishExample().GetProperty("documentationFit").GetString());
    }

    /// <summary>
    /// Same guard as the worker-instruction test: an example block this file cannot read must fail
    /// loudly instead of being skipped, and every block must be one of the two this test classifies.
    /// </summary>
    [Fact]
    public void EveryJsonExampleInTheShippedOrchestratorInstructions_IsReadAndClassified()
    {
        var instructions = OrchestratorInstructions();
        var examples = JsonExamplePattern.Matches(instructions);
        var fenceOpenings = JsonExampleFenceOpeningPattern.Matches(instructions);

        Assert.Equal(fenceOpenings.Count, examples.Count);
        Assert.Equal(2, examples.Count);
        Assert.Single(Examples(), static example => example.TryGetProperty("report_json", out _));
        Assert.Single(Examples(), static example => example.TryGetProperty("matched", out _));
    }

    private static JsonElement PublishExample() =>
        Examples().Single(static example => example.TryGetProperty("report_json", out _)).GetProperty("report_json");

    private static JsonElement MemoryResultExample() =>
        Examples().Single(static example => example.TryGetProperty("matched", out _));

    private static JsonElement MemoryRoleMatchedExample()
    {
        var configPath = TriageConfigurationFileLocator.Shipped();
        using var configDocument = JsonDocument.Parse(File.ReadAllText(configPath));
        var instructions = File.ReadAllText(TriageConfigurationFileLocator.ResolveReference(
            configPath,
            configDocument.RootElement.GetProperty("Roles").GetProperty("memory").GetProperty("Instructions").GetString()!));
        return ParseExamples(instructions)
            .Single(static example => example.GetProperty("items").EnumerateArray().Any());
    }

    private static List<JsonElement> Examples() => ParseExamples(OrchestratorInstructions());

    private static List<JsonElement> ParseExamples(string instructions) =>
        JsonExamplePattern.Matches(instructions)
            .Select(static match => JsonDocument.Parse(match.Groups["example"].Value).RootElement.Clone())
            .ToList();

    private static string OrchestratorInstructions()
    {
        var configPath = TriageConfigurationFileLocator.Shipped();
        using var configDocument = JsonDocument.Parse(File.ReadAllText(configPath));
        return File.ReadAllText(TriageConfigurationFileLocator.ResolveReference(
            configPath,
            configDocument.RootElement.GetProperty("Orchestrator").GetProperty("Instructions").GetString()!));
    }

    private static List<string> PublishReportRequiredProperties() =>
        OrchestratorToolDefinitions.Create(TestTriageConfiguration.Create())
            .Single(static tool => string.Equals(tool.Name, OrchestratorToolNames.PublishReport, StringComparison.Ordinal))
            .InputSchema
            .GetProperty("properties")
            .GetProperty("report_json")
            .GetProperty("required")
            .EnumerateArray()
            .Select(static value => value.GetString()!)
            .ToList();

    /// <summary>
    /// The shipped configuration's own <c>CurrentReleases</c> map, which is what decides whether a
    /// seeded document is current, stale or unassessable.
    /// </summary>
    private static TriageConfiguration ShippedConfiguration()
    {
        using var configDocument = JsonDocument.Parse(File.ReadAllText(TriageConfigurationFileLocator.Shipped()));
        var currentReleases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in configDocument.RootElement.GetProperty("CurrentReleases").EnumerateObject())
        {
            currentReleases[entry.Name] = entry.Value.GetString()!;
        }

        return TestTriageConfiguration.Create() with { CurrentReleases = currentReleases };
    }

    private static List<MemorySearchMatch> ShippedSampleDocuments()
    {
        var samples = Path.Combine(RepositoryRootLocator.Find(), "samples");
        var documents = new List<MemorySearchMatch>();
        foreach (var path in new[]
        {
            Path.Combine(samples, "runbooks", "checkout-timeout.md"),
            Path.Combine(samples, "incidents", "checkout-timeout-known-incident.md")
        })
        {
            var (metadata, _) = MemorySeedFrontmatterParser.Parse(path, File.ReadAllText(path));
            documents.Add(new MemorySearchMatch(
                Guid.NewGuid(),
                Guid.NewGuid(),
                metadata.Kind ?? "runbook",
                Path.GetFileName(path),
                Path.GetFileNameWithoutExtension(path),
                0,
                "sample text",
                0.9,
                metadata.ServiceName,
                metadata.Component,
                metadata.ReleaseName));
        }

        return documents;
    }
}
