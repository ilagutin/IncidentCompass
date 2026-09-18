using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The in-process embedding model is the shipped default only while the files that ship it agree: the
/// triage configuration routes memory embeddings to the local provider entry under the model the host
/// options install, both compose files default the host provider to <c>LocalOnnx</c> and give the
/// Worker its model volume, the Worker image creates that mount point for its non-root user, and the
/// production sample names the same provider and model. A drift in any one of them leaves a fresh host
/// with a model mismatch or an unwritable model directory while every other test stays green. These
/// assertions read the files themselves and need no Docker.
/// </summary>
public sealed partial class ShippedEmbeddingDefaultTests
{
    private const string LocalProviderId = "local-embed";
    private const string ProviderDefault = "IncidentCompass__Embeddings__Provider: ${INCIDENTCOMPASS_EMBEDDINGS_PROVIDER:-LocalOnnx}";
    private const string ProviderIdDefault = "INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID: ${INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID:-local-embed}";
    private const string ModelDirectory = "IncidentCompass__Embeddings__LocalOnnx__ModelDirectory: /app/models";
    private const string JudgeModelDirectory = "IncidentCompass__RelevanceJudge__LocalOnnx__ModelDirectory";
    private const string JudgeProvider = "IncidentCompass__RelevanceJudge__Provider";

    [Fact]
    public void ShippedConfiguration_RoutesMemoryEmbeddingsToTheLocalModelAndKeepsChatOnTheOpenAiProvider()
    {
        var root = JsonNode.Parse(Read("config", "incidentcompass.config.json"))!;
        var routes = root["Routes"]!;

        Assert.Equal("LocalOnnx", root["Providers"]![LocalProviderId]!["Kind"]!.GetValue<string>());
        Assert.Equal(
            "${INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID:-" + LocalProviderId + "}",
            routes["memory-embed"]!["ProviderId"]!.GetValue<string>());
        Assert.Equal(
            "${INCIDENTCOMPASS_EMBEDDINGS_MODEL:-" + LocalOnnxEmbeddingOptions.DefaultModelId + "}",
            routes["memory-embed"]!["Model"]!.GetValue<string>());
        Assert.Equal("local-oai", routes["analysis-chat"]!["ProviderId"]!.GetValue<string>());
        Assert.Equal("local-oai", routes["report-chat"]!["ProviderId"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("docker-compose.yml")]
    [InlineData("compose.production.yml")]
    public void ComposeFile_DefaultsToTheLocalModelAndMountsItsVolumeOnTheWorkerOnly(string fileName)
    {
        var compose = Read(fileName);

        Assert.Contains(ProviderDefault, compose, StringComparison.Ordinal);
        Assert.Contains(ProviderIdDefault, compose, StringComparison.Ordinal);
        var worker = ReadServiceBlock(fileName, "worker");
        Assert.Contains(ModelDirectory, worker, StringComparison.Ordinal);
        Assert.Matches(ModelVolumeMountPattern(), worker);
        Assert.DoesNotMatch(ModelVolumeMountPattern(), ReadServiceBlock(fileName, "api"));
        Assert.Contains("  embedding-models:", ReadTopLevelBlock(fileName, "volumes"));
    }

    [Fact]
    public void LocalDemoCompose_DefaultsTheRouteModelToTheModelTheWorkerInstalls()
    {
        Assert.Contains(
            "INCIDENTCOMPASS_EMBEDDINGS_MODEL: ${INCIDENTCOMPASS_EMBEDDINGS_MODEL:-" + LocalOnnxEmbeddingOptions.DefaultModelId + "}",
            Read("docker-compose.yml"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Compose cannot require a value only for one provider, so the OpenAI-compatible embedding values
    /// must be optional in the overlay; the preflight requires them when that provider is chosen.
    /// </summary>
    [Theory]
    [InlineData("INCIDENTCOMPASS_EMBEDDINGS_BASE_URL")]
    [InlineData("INCIDENTCOMPASS_EMBEDDINGS_PATH")]
    [InlineData("INCIDENTCOMPASS_EMBEDDINGS_API_KEY")]
    public void ProductionOverlay_LeavesOpenAiCompatibleEmbeddingValuesOptional(string variable)
    {
        var overlay = Read("compose.production.yml");

        Assert.Contains("${" + variable + ":-}", overlay, StringComparison.Ordinal);
        Assert.DoesNotContain("${" + variable + ":?", overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// The mock stack mocks every model, so its worker runs the mock relevance judge: confirmation
    /// needs a judge, and without one the mock demo could never reach a memory-based known-incident
    /// report. Compose merges <c>environment</c> maps, so the overlay also resets the local judge's
    /// model directory it would inherit from the demo file; the mock judge composes no install pass,
    /// and the reset keeps that true even if the provider line is ever removed.
    /// </summary>
    [Fact]
    public void MockOverlay_RunsTheMockRelevanceJudgeAndResetsTheLocalJudgeModelDirectory()
    {
        var worker = ReadServiceBlock("compose.mock.yml", "worker");

        Assert.Contains(JudgeProvider + ": Mock", worker, StringComparison.Ordinal);
        Assert.Contains(JudgeModelDirectory + ": \"\"", worker, StringComparison.Ordinal);
        Assert.DoesNotContain(JudgeProvider, ReadServiceBlock("compose.mock.yml", "api"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The evaluation stack is where real-model evaluations run, so it runs the product as shipped,
    /// relevance judge included: it inherits the demo file's judge directory rather than resetting it,
    /// and names no other judge provider. Without a judge nothing retrieved from memory is confirmed,
    /// and its known and stale cases could never reach a memory-based known-incident report with any
    /// real model. The cost is the cross-encoder's install on a fresh volume, about 544 MiB.
    /// <para>
    /// The check is textual, because rendering the merged configuration needs Docker and this class
    /// runs without it. It does not catch a directory reset or a provider set in a way these string
    /// searches miss, such as a YAML anchor, an <c>env_file</c> entry or a later override file; the
    /// production stack's rendered configuration is asserted in <c>ProductionOperationsTests</c>.
    /// </para>
    /// </summary>
    [Fact]
    public void EvaluationOverlay_InheritsTheShippedRelevanceJudge()
    {
        var worker = ReadServiceBlock("compose.evaluation.yml", "worker");

        Assert.Contains(
            JudgeModelDirectory + ": /app/models/relevance-judge",
            ReadServiceBlock("docker-compose.yml", "worker"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(JudgeModelDirectory, worker, StringComparison.Ordinal);
        Assert.DoesNotContain(JudgeProvider, worker, StringComparison.Ordinal);
        Assert.DoesNotContain(JudgeProvider, Read("docker-compose.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluationOverlay_KeepsTheOpenAiCompatibleAdapterItsConfigurationRoutesTo()
    {
        var evaluation = JsonNode.Parse(Read("evaluations", "triage", "incidentcompass.config.json"))!;
        var route = evaluation["Routes"]!["memory-embed"]!;
        var providerId = route["ProviderId"]!.GetValue<string>();
        var model = route["Model"]!.GetValue<string>();

        Assert.DoesNotContain("${", providerId, StringComparison.Ordinal);
        Assert.Equal("OpenAICompatible", evaluation["Providers"]![providerId]!["Kind"]!.GetValue<string>());
        Assert.Contains(
            "IncidentCompass__Embeddings__Provider: OpenAICompatible",
            ReadServiceBlock("compose.evaluation.yml", "worker"),
            StringComparison.Ordinal);

        // The base file defaults the model variable to the in-process model's id. Every evaluation service
        // that expands the route must default it to the evaluation route's own model instead, the one the
        // OpenAI-compatible server is asked for and the tester records.
        Assert.StartsWith("${INCIDENTCOMPASS_EMBEDDINGS_MODEL:-", model, StringComparison.Ordinal);
        Assert.DoesNotContain(LocalOnnxEmbeddingOptions.DefaultModelId, model, StringComparison.Ordinal);
        foreach (var service in new[] { "api", "worker", "tester" })
        {
            Assert.Contains(
                "INCIDENTCOMPASS_EMBEDDINGS_MODEL: " + model,
                ReadServiceBlock("compose.evaluation.yml", service),
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WorkerImage_CreatesTheModelMountPointForItsUserAndShipsNoModel()
    {
        var dockerfile = Read("src", "IncidentCompass.Worker", "Dockerfile");
        var created = dockerfile.IndexOf("mkdir -p /app/models", StringComparison.Ordinal);

        Assert.InRange(created, 0, dockerfile.IndexOf("chown -R incidentcompass:incidentcompass /app", StringComparison.Ordinal));
        Assert.InRange(created, 0, dockerfile.IndexOf("USER incidentcompass", StringComparison.Ordinal));
        Assert.DoesNotMatch(CopyIntoModelsPattern(), dockerfile);
    }

    [Fact]
    public void ProductionSample_NamesTheLocalProviderAndTheShippedModel()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRootLocator.Find(), ".env.production.example"));

        Assert.Contains("INCIDENTCOMPASS_EMBEDDINGS_PROVIDER=LocalOnnx", lines);
        Assert.Contains("INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID=" + LocalProviderId, lines);
        Assert.Contains("INCIDENTCOMPASS_EMBEDDINGS_MODEL=" + LocalOnnxEmbeddingOptions.DefaultModelId, lines);
    }

    /// <summary>
    /// The quickstart copies <c>.env.example</c> to <c>.env</c>, whose values replace the demo compose
    /// defaults. An example that named another embedding model or provider would turn that first step
    /// into a model mismatch, so every embedding value it carries must be the compose default.
    /// </summary>
    [Fact]
    public void LocalExample_CarriesTheLocalDemoComposeEmbeddingDefaults()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRootLocator.Find(), ".env.example"));
        var compose = Read("docker-compose.yml");

        foreach (var (name, value) in new[]
                 {
                     ("INCIDENTCOMPASS_EMBEDDINGS_PROVIDER", "LocalOnnx"),
                     ("INCIDENTCOMPASS_EMBEDDINGS_PROVIDER_ID", LocalProviderId),
                     ("INCIDENTCOMPASS_EMBEDDINGS_MODEL", LocalOnnxEmbeddingOptions.DefaultModelId),
                     ("INCIDENTCOMPASS_EMBEDDINGS_BASE_URL", "http://host.docker.internal:1234"),
                     ("INCIDENTCOMPASS_EMBEDDINGS_PATH", "/v1/embeddings"),
                     ("INCIDENTCOMPASS_EMBEDDINGS_API_KEY", "local-dev-key")
                 })
        {
            Assert.Contains(name + "=" + value, lines);
            Assert.Contains("${" + name + ":-" + value + "}", compose, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(
        @"(^[ \t]*-[ \t]*embedding-models:/app/models[ \t]*$)|(source:[ \t]*embedding-models[ \t]*\r?\n[ \t]*target:[ \t]*/app/models)",
        RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex ModelVolumeMountPattern();

    [GeneratedRegex(@"^COPY\b.*(\./models|/app/models)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex CopyIntoModelsPattern();

    private static string Read(params string[] relativePath) =>
        File.ReadAllText(Path.Combine([RepositoryRootLocator.Find(), .. relativePath]));

    /// <summary>
    /// The body of one service declaration: indented by four spaces or more, ending at the next
    /// non-blank line that is not.
    /// </summary>
    private static string ReadServiceBlock(string fileName, string serviceName) =>
        ReadIndentedBlock(fileName, "  " + serviceName + ":", "    ");

    private static string ReadTopLevelBlock(string fileName, string key) =>
        ReadIndentedBlock(fileName, key + ":", "  ");

    private static string ReadIndentedBlock(string fileName, string header, string bodyIndent)
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRootLocator.Find(), fileName));
        var start = Array.IndexOf(lines, header);
        Assert.True(start >= 0, $"'{fileName}' has no '{header.Trim()}' declaration.");

        var body = new List<string>();
        for (var index = start + 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Trim().Length > 0 && !line.StartsWith(bodyIndent, StringComparison.Ordinal))
            {
                break;
            }

            body.Add(line);
        }

        return string.Join('\n', body);
    }
}
