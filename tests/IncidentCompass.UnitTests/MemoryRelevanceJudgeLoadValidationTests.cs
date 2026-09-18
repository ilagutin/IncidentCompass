using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Application.Tickets;
using IncidentCompass.Infrastructure.Intake;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The three relevance-judge keys of <c>Tools.memory_search</c> are optional, belong to that tool
/// alone, and are bounded. A configuration file that sets none of them is hashed and snapshotted from
/// exactly its own content, so adding them moved no shipped configuration hash.
/// </summary>
public sealed class MemoryRelevanceJudgeLoadValidationTests
{
    private const string ReadTool = "memory_search";
    private const string OtherReadTool = "source_lookup";
    private const string ActionTool = TicketCreateTool.ToolId;

    private static readonly string[] SettingNames =
    [
        MemoryRelevanceJudgeSetting.ModeSettingName,
        MemoryRelevanceJudgeSetting.ConfirmScoreSettingName,
        MemoryRelevanceJudgeSetting.FloorScoreSettingName
    ];

    [Theory]
    [InlineData(MemoryRelevanceJudgeSetting.Off, "Off")]
    [InlineData(MemoryRelevanceJudgeSetting.On, "On")]
    public void Materialize_KnownMode_LoadsAndResolvesToItsMode(string value, string expectedMode)
    {
        var node = ConfigNode();
        Tool(node, ReadTool)[MemoryRelevanceJudgeSetting.ModeSettingName] = value;

        var configuration = Materialize(node);

        Assert.Equal(value, configuration.Tools[ReadTool].RelevanceJudge);
        Assert.Equal(
            expectedMode,
            MemoryRelevanceJudgeSetting.Resolve(configuration.Tools[ReadTool]).Mode.ToString());
    }

    [Fact]
    public void Materialize_AbsentKeys_ResolveToTheShippedDefaults()
    {
        var configuration = Materialize(ConfigNode());
        var tool = configuration.Tools[ReadTool];

        Assert.Null(tool.RelevanceJudge);
        Assert.Null(tool.RelevanceConfirmScore);
        Assert.Null(tool.RelevanceFloorScore);
        var resolved = MemoryRelevanceJudgeSetting.Resolve(tool);
        Assert.Equal("On", resolved.Mode.ToString());
        Assert.Equal(0.75, resolved.ConfirmScore);
        Assert.Equal(-0.25, resolved.FloorScore);
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("ON")]
    [InlineData("enabled")]
    [InlineData("true")]
    [InlineData("")]
    public void Materialize_UnknownMode_FailsNamingTheSettingPath(string value)
    {
        var node = ConfigNode();
        Tool(node, ReadTool)[MemoryRelevanceJudgeSetting.ModeSettingName] = value;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() => Materialize(node));

        Assert.Contains(
            "Tools." + ReadTool + "." + MemoryRelevanceJudgeSetting.ModeSettingName,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MemoryRelevanceJudgeSetting.ConfirmScoreSettingName, 50.5)]
    [InlineData(MemoryRelevanceJudgeSetting.ConfirmScoreSettingName, -50.5)]
    [InlineData(MemoryRelevanceJudgeSetting.FloorScoreSettingName, 115.0)]
    [InlineData(MemoryRelevanceJudgeSetting.FloorScoreSettingName, -1000.0)]
    public void Materialize_ScoreOutsideTheBoundedRange_FailsNamingTheSettingPath(string settingName, double value)
    {
        var node = ConfigNode();
        Tool(node, ReadTool)[settingName] = value;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() => Materialize(node));

        Assert.Contains("Tools." + ReadTool + "." + settingName, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The floor must sit strictly below the confirm score. An equal pair is the quiet mistake: every
    /// candidate the judge admits is then confirmed, the <c>low</c> band cannot occur, and the
    /// publication rule that refuses a <c>KnownIncident</c> resting only on unconfirmed memory becomes
    /// a rule that can never fire, under a configuration that would otherwise load without complaint.
    /// </summary>
    [Theory]
    [InlineData(1.15, 1.15)]
    [InlineData(0.0, 0.0)]
    [InlineData(-3.0, -3.0)]
    public void Materialize_FloorEqualToTheConfirmScore_IsRefused(double confirmScore, double floorScore)
    {
        var node = ConfigNode();
        Tool(node, ReadTool)[MemoryRelevanceJudgeSetting.ConfirmScoreSettingName] = confirmScore;
        Tool(node, ReadTool)[MemoryRelevanceJudgeSetting.FloorScoreSettingName] = floorScore;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() => Materialize(node));

        Assert.Contains(
            "Tools." + ReadTool + "." + MemoryRelevanceJudgeSetting.FloorScoreSettingName,
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The other side of the same boundary: a floor just below the confirm score leaves a band that
    /// can be reached, so it loads and resolves to exactly the pair that was written.
    /// </summary>
    [Theory]
    [InlineData(1.15, 1.14)]
    [InlineData(0.0, -0.01)]
    public void Materialize_FloorJustBelowTheConfirmScore_Loads(double confirmScore, double floorScore)
    {
        var node = ConfigNode();
        Tool(node, ReadTool)[MemoryRelevanceJudgeSetting.ConfirmScoreSettingName] = confirmScore;
        Tool(node, ReadTool)[MemoryRelevanceJudgeSetting.FloorScoreSettingName] = floorScore;

        var resolved = MemoryRelevanceJudgeSetting.Resolve(Materialize(node).Tools[ReadTool]);

        Assert.Equal(confirmScore, resolved.ConfirmScore);
        Assert.Equal(floorScore, resolved.FloorScore);
        Assert.True(resolved.FloorScore < resolved.ConfirmScore);
    }

    /// <summary>
    /// The resolver is the snapshot-side half of the same check, and it fails closed rather than
    /// resolving a pair a loader should never have written.
    /// </summary>
    [Fact]
    public void Resolve_FloorEqualToTheConfirmScore_FailsClosed()
    {
        var tool = Materialize(ConfigNode()).Tools[ReadTool] with
        {
            RelevanceConfirmScore = 0.5,
            RelevanceFloorScore = 0.5
        };

        var exception = Assert.Throws<InvalidOperationException>(() => MemoryRelevanceJudgeSetting.Resolve(tool));

        Assert.Contains("no admitted candidate could be unconfirmed", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1.15, 2.0)]
    [InlineData(-3.0, 0.0)]
    public void Materialize_FloorAboveTheConfirmScore_IsRefused(double confirmScore, double floorScore)
    {
        var node = ConfigNode();
        Tool(node, ReadTool)[MemoryRelevanceJudgeSetting.ConfirmScoreSettingName] = confirmScore;
        Tool(node, ReadTool)[MemoryRelevanceJudgeSetting.FloorScoreSettingName] = floorScore;

        var exception = Assert.Throws<TriageConfigurationLoadException>(() => Materialize(node));

        Assert.Contains(
            "Tools." + ReadTool + "." + MemoryRelevanceJudgeSetting.FloorScoreSettingName,
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The default confirm score is the comparison point for a floor the operator sets alone, so a
    /// floor above it is refused even when no confirm score is written at all.
    /// </summary>
    [Fact]
    public void Materialize_FloorAboveTheDefaultConfirmScoreWithNoConfirmScoreSet_IsRefused()
    {
        var node = ConfigNode();
        Tool(node, ReadTool)[MemoryRelevanceJudgeSetting.FloorScoreSettingName] = 2.0;

        Assert.Throws<TriageConfigurationLoadException>(() => Materialize(node));
    }

    public static TheoryData<string, string, object> ForeignToolCases()
    {
        var cases = new TheoryData<string, string, object>();
        foreach (var toolName in new[] { OtherReadTool, ActionTool })
        {
            cases.Add(toolName, MemoryRelevanceJudgeSetting.ModeSettingName, MemoryRelevanceJudgeSetting.On);
            cases.Add(toolName, MemoryRelevanceJudgeSetting.ConfirmScoreSettingName, 1.15);
            cases.Add(toolName, MemoryRelevanceJudgeSetting.FloorScoreSettingName, 0.0);
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(ForeignToolCases))]
    public void Materialize_KeyOnAnotherTool_IsRefused(string toolName, string settingName, object value)
    {
        var node = ConfigNode();
        Tool(node, toolName)[settingName] = JsonValue.Create(value);

        var exception = Assert.Throws<TriageConfigurationLoadException>(() => Materialize(node));

        Assert.Contains("Tools." + toolName, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shipped file sets none of the three keys. Loaded through the real file repository the
    /// snapshot it persists is the file's own content with no key added, and the hash it reports is the
    /// hash of that content, so a hash only moves when an operator actually sets one.
    /// </summary>
    [Fact]
    public async Task FileRepository_ShippedConfigurationWithoutTheKeys_HashesExactlyTheFileContent()
    {
        using var fixture = ShippedConfigDirectory.Copy("relevance-judge-hash");
        var fileNode = JsonNode.Parse(
            await File.ReadAllTextAsync(fixture.ConfigPath, TestContext.Current.CancellationToken))!;
        foreach (var (_, tool) in (JsonObject)fileNode["Tools"]!)
        {
            foreach (var settingName in SettingNames)
            {
                Assert.False(((JsonObject)tool!).ContainsKey(settingName));
            }
        }

        var snapshots = new RecordingSnapshotStore();
        var configuration = await ShippedConfigTestSupport
            .CreateRepository(fixture.RootPath, snapshots)
            .GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.All(configuration.Tools.Values, tool =>
        {
            Assert.Null(tool.RelevanceJudge);
            Assert.Null(tool.RelevanceConfirmScore);
            Assert.Null(tool.RelevanceFloorScore);
        });
        EnvironmentPlaceholderExpander.Expand(fileNode);
        Assert.True(JsonNode.DeepEquals(fileNode, snapshots.ConfigNode));
        Assert.Equal(
            CanonicalJsonSerializer.ComputeSha256Hex(
                CanonicalJsonSerializer.Canonicalize(fileNode),
                CanonicalJsonSerializer.Canonicalize(snapshots.InstructionsNode!)),
            configuration.ConfigHash);

        var withJudge = (JsonObject)JsonNode.Parse(
            await File.ReadAllTextAsync(fixture.ConfigPath, TestContext.Current.CancellationToken))!;
        ((JsonObject)withJudge["Tools"]![ReadTool]!)[MemoryRelevanceJudgeSetting.ModeSettingName] =
            MemoryRelevanceJudgeSetting.Off;
        await File.WriteAllTextAsync(
            fixture.ConfigPath, withJudge.ToJsonString(), TestContext.Current.CancellationToken);
        var changed = await ShippedConfigTestSupport
            .CreateRepository(fixture.RootPath, new RecordingSnapshotStore())
            .GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.Equal(MemoryRelevanceJudgeSetting.Off, changed.Tools[ReadTool].RelevanceJudge);
        Assert.NotEqual(configuration.ConfigHash, changed.ConfigHash);
    }

    public static TheoryData<string, string, object?, bool> SchemaCases()
    {
        var cases = new TheoryData<string, string, object?, bool>
        {
            { ReadTool, MemoryRelevanceJudgeSetting.ModeSettingName, null, true },
            { ReadTool, MemoryRelevanceJudgeSetting.ModeSettingName, MemoryRelevanceJudgeSetting.Off, true },
            { ReadTool, MemoryRelevanceJudgeSetting.ModeSettingName, MemoryRelevanceJudgeSetting.On, true },
            { ReadTool, MemoryRelevanceJudgeSetting.ModeSettingName, "On", false },
            { ReadTool, MemoryRelevanceJudgeSetting.ModeSettingName, "", false },
            { ReadTool, MemoryRelevanceJudgeSetting.ConfirmScoreSettingName, 1.15, true },
            { ReadTool, MemoryRelevanceJudgeSetting.ConfirmScoreSettingName, 50.5, false },
            { ReadTool, MemoryRelevanceJudgeSetting.FloorScoreSettingName, 0.0, true },
            { ReadTool, MemoryRelevanceJudgeSetting.FloorScoreSettingName, -50.5, false }
        };
        return cases;
    }

    [Theory]
    [MemberData(nameof(SchemaCases))]
    public void PublishedSchema_AcceptsTheSameValuesAsLoadValidation(
        string toolName,
        string settingName,
        object? value,
        bool expectedValid)
    {
        var configuration = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryRootLocator.Find(),
            "config",
            "incidentcompass.config.json")))!;
        if (value is not null)
        {
            ((JsonObject)configuration["Tools"]![toolName]!)[settingName] = JsonValue.Create(value);
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
