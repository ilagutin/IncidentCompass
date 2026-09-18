using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.Intake;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// <c>Tools.memory_search.VectorOnlyFallback</c> is optional, belongs to that tool alone, and accepts
/// exactly three spellings. A configuration file that does not set it is hashed and snapshotted from
/// exactly its own content, so adding the setting moved no shipped configuration hash.
/// </summary>
public sealed class MemoryVectorOnlyFallbackLoadValidationTests
{
    private const string ReadTool = "memory_search";
    private const string OtherReadTool = "source_lookup";
    private const string ActionTool = TicketCreateTool.ToolId;
    private const string SettingPath = "Tools." + ReadTool + ".VectorOnlyFallback";

    [Theory]
    [InlineData("off", "Off")]
    [InlineData("foreign_script", "ForeignScript")]
    [InlineData("always", "Always")]
    public void Materialize_KnownValue_LoadsAndResolvesToItsMode(string value, string expectedMode)
    {
        var node = ConfigNode();
        Tool(node, ReadTool)["VectorOnlyFallback"] = value;

        var configuration = Materialize(node);

        Assert.Equal(value, configuration.Tools[ReadTool].VectorOnlyFallback);
        Assert.Equal(
            expectedMode,
            MemorySearchVectorOnlyFallbackSetting.Resolve(configuration.Tools[ReadTool].VectorOnlyFallback).ToString());
    }

    [Fact]
    public void Materialize_AbsentKey_ResolvesToForeignScript()
    {
        var configuration = Materialize(ConfigNode());

        Assert.Null(configuration.Tools[ReadTool].VectorOnlyFallback);
        Assert.Equal(
            MemorySearchVectorOnlyFallback.ForeignScript,
            MemorySearchVectorOnlyFallbackSetting.Resolve(configuration.Tools[ReadTool].VectorOnlyFallback));
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("FOREIGN_SCRIPT")]
    [InlineData("foreign script")]
    [InlineData("vector")]
    [InlineData("")]
    public void Materialize_UnknownValue_FailsNamingTheSettingPath(string value)
    {
        var node = ConfigNode();
        Tool(node, ReadTool)["VectorOnlyFallback"] = value;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() => Materialize(node));

        Assert.Contains(SettingPath, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OtherReadTool)]
    [InlineData(ActionTool)]
    public void Materialize_KeyOnAnotherTool_IsRefused(string toolName)
    {
        var node = ConfigNode();
        Tool(node, toolName)["VectorOnlyFallback"] = "always";

        var exception = Assert.Throws<TriageConfigurationLoadException>(() => Materialize(node));

        Assert.Contains("Tools." + toolName, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shipped file sets no fallback. Loaded through the real file repository the snapshot it
    /// persists is the file's own content with no key added, and the hash it reports is the hash of that
    /// content. The same file with the key set hashes differently, so a hash only moves when an operator
    /// actually sets it.
    /// </summary>
    [Fact]
    public async Task FileRepository_ShippedConfigurationWithoutTheKey_HashesExactlyTheFileContent()
    {
        using var fixture = ShippedConfigDirectory.Copy("vector-only-fallback-hash");
        var fileNode = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigPath, TestContext.Current.CancellationToken))!;
        foreach (var (_, tool) in (JsonObject)fileNode["Tools"]!)
        {
            Assert.False(((JsonObject)tool!).ContainsKey("VectorOnlyFallback"));
        }

        var snapshots = new RecordingSnapshotStore();
        var configuration = await ShippedConfigTestSupport.CreateRepository(fixture.RootPath, snapshots)
            .GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.All(configuration.Tools.Values, tool => Assert.Null(tool.VectorOnlyFallback));
        EnvironmentPlaceholderExpander.Expand(fileNode);
        Assert.True(JsonNode.DeepEquals(fileNode, snapshots.ConfigNode));
        Assert.Equal(
            CanonicalJsonSerializer.ComputeSha256Hex(
                CanonicalJsonSerializer.Canonicalize(fileNode),
                CanonicalJsonSerializer.Canonicalize(snapshots.InstructionsNode!)),
            configuration.ConfigHash);

        var withFallback = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigPath, TestContext.Current.CancellationToken))!;
        ((JsonObject)withFallback["Tools"]![ReadTool]!)["VectorOnlyFallback"] = "always";
        await File.WriteAllTextAsync(fixture.ConfigPath, withFallback.ToJsonString(), TestContext.Current.CancellationToken);
        var changed = await ShippedConfigTestSupport
            .CreateRepository(fixture.RootPath, new RecordingSnapshotStore())
            .GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.Equal("always", changed.Tools[ReadTool].VectorOnlyFallback);
        Assert.NotEqual(configuration.ConfigHash, changed.ConfigHash);
    }

    public static TheoryData<string, string?, bool> SchemaCases => new()
    {
        { ReadTool, null, true },
        { ReadTool, "off", true },
        { ReadTool, "foreign_script", true },
        { ReadTool, "always", true },
        { ReadTool, "Off", false },
        { ReadTool, "vector", false },
        { ReadTool, "", false }
    };

    [Theory]
    [MemberData(nameof(SchemaCases))]
    public void PublishedSchema_AcceptsTheSameSpellingsAsLoadValidation(string toolName, string? value, bool expectedValid)
    {
        var configuration = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryRootLocator.Find(),
            "config",
            "incidentcompass.config.json")))!;
        if (value is not null)
        {
            ((JsonObject)configuration["Tools"]![toolName]!)["VectorOnlyFallback"] = value;
        }

        Assert.Equal(expectedValid, TriageConfigSchemaTests.EvaluateFixture(configuration).IsValid);
    }

    private static TriageConfiguration Materialize(JsonObject node) =>
        CreateMaterializer().Materialize("hash-1", node, ResolvedReferences());

    private static JsonObject ConfigNode()
    {
        var node = OrchestratorAttemptDurationLoadValidationTests.ConfigNode();
        var tools = (JsonObject)node["Tools"]!;
        tools[OtherReadTool] = new JsonObject { ["Kind"] = "internal" };
        tools[ActionTool] = new JsonObject
        {
            ["Kind"] = "external_action",
            ["Category"] = "ticket_create",
            ["LogicalTargetId"] = TicketCreateTool.LogicalTargetId,
            ["Mode"] = "disabled"
        };
        return node;
    }

    private static JsonObject Tool(JsonObject node, string toolName) =>
        (JsonObject)((JsonObject)node["Tools"]!)[toolName]!;

    private static JsonObject ResolvedReferences() =>
        OrchestratorAttemptDurationLoadValidationTests.ResolvedReferences();

    private static TriageConfigurationMaterializer CreateMaterializer() =>
        ShippedConfigTestSupport.CreateMaterializer();
}
