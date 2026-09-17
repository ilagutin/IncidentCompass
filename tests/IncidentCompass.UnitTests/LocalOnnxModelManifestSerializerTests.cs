using System.Text.Json.Nodes;
using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Every field the manifest's kind calls for is required. A manifest missing one, or carrying a
/// value the adapter cannot use, is refused with the manifest code instead of being read with a
/// default: a missing <c>normalize</c> would otherwise read as false and produce unnormalized
/// vectors. The kind decides which fields those are, and the schema a shipped release wrote is read
/// as the embedding manifest it always was.
/// </summary>
public sealed class LocalOnnxModelManifestSerializerTests : IDisposable
{
    private readonly LocalOnnxTestDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public async Task WriteThenRead_RoundTripsTheManifest()
    {
        var manifest = EmbeddingManifest();

        await LocalOnnxModelManifestSerializer.WriteAtomicallyAsync(ManifestPath, manifest, TestContext.Current.CancellationToken);

        Assert.Equal(manifest, await LocalOnnxModelManifestSerializer.ReadAsync(ManifestPath, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A written manifest names this version's schema and what its artifacts are for, so the next
    /// reader does not have to infer the kind from the fields that happen to be present.
    /// </summary>
    [Fact]
    public void Write_UsesTheCurrentSchemaAndNamesTheKind()
    {
        var manifest = EmbeddingManifest();

        Assert.Equal(LocalOnnxModelManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        Assert.Equal(LocalOnnxModelManifest.EmbeddingKind, manifest.Kind);
    }

    /// <summary>
    /// A shipped release wrote schema 1 manifests, which have no kind field, onto operator volumes.
    /// One is still read, as the embedding manifest it always was, so upgrading a host does not
    /// orphan the model already installed on it.
    /// </summary>
    [Fact]
    public async Task Read_ReadsASchemaOneManifestAsAnEmbeddingManifest()
    {
        await WriteManifestAsync(EmbeddingManifest(), manifest =>
        {
            manifest["schemaVersion"] = JsonValue.Create(LocalOnnxModelManifest.LegacyEmbeddingSchemaVersion);
            Assert.True(manifest.Remove("kind"), "The written manifest has no field kind.");
        });

        var read = await LocalOnnxModelManifestSerializer.ReadAsync(ManifestPath, TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Equal(LocalOnnxModelManifest.LegacyEmbeddingSchemaVersion, read.SchemaVersion);
        Assert.Equal(LocalOnnxModelManifest.EmbeddingKind, read.Kind);
        Assert.Equal(8, read.GetEmbeddingProfile().Dimensions);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsAManifestThatIsNotAnEmbeddingManifest()
    {
        var manifest = JudgeManifest();

        await LocalOnnxModelManifestSerializer.WriteAtomicallyAsync(ManifestPath, manifest, TestContext.Current.CancellationToken);

        var read = await LocalOnnxModelManifestSerializer.ReadAsync(ManifestPath, TestContext.Current.CancellationToken);
        Assert.Equal(manifest, read);
        Assert.Equal(LocalOnnxModelManifest.RelevanceJudgeKind, read!.Kind);
        Assert.Null(read.Dimensions);
        Assert.Null(read.Pooling);
    }

    /// <summary>
    /// The settings only an embedding model has are not asked of any other kind, while both files
    /// still are: a kind that names no model or no tokenizer is as incomplete as ever.
    /// </summary>
    [Theory]
    [InlineData("dimensions")]
    [InlineData("pooling")]
    [InlineData("normalize")]
    [InlineData("queryPrefix")]
    [InlineData("passagePrefix")]
    public async Task Read_AcceptsARelevanceJudgeManifestWithoutTheEmbeddingFields(string field)
    {
        await WriteManifestAsync(JudgeManifest(), manifest => RemoveField(manifest, field));

        var read = await LocalOnnxModelManifestSerializer.ReadAsync(ManifestPath, TestContext.Current.CancellationToken);

        Assert.Equal(LocalOnnxModelManifest.RelevanceJudgeKind, read!.Kind);
    }

    [Theory]
    [InlineData("modelFile")]
    [InlineData("tokenizerFile")]
    [InlineData("modelFile.sha256")]
    [InlineData("tokenizerFile.kind")]
    public async Task Read_RefusesARelevanceJudgeManifestMissingAnArtifactField(string field)
    {
        await WriteManifestAsync(JudgeManifest(), manifest => RemoveField(manifest, field));

        var exception = await Assert.ThrowsAsync<LocalOnnxModelStoreException>(() =>
            LocalOnnxModelManifestSerializer.ReadAsync(ManifestPath, TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxModelErrorCodes.ManifestInvalid, exception.ErrorCode);
    }

    /// <summary>
    /// A manifest this version could not read back is refused before anything is created, so an
    /// install never leaves a model directory holding a manifest that refuses itself on the next
    /// read.
    /// </summary>
    [Fact]
    public async Task Write_RefusesAManifestThisVersionCouldNotReadBackAndCreatesNothing()
    {
        var incomplete = EmbeddingManifest() with { Pooling = null };

        var exception = await Assert.ThrowsAsync<LocalOnnxModelStoreException>(() =>
            LocalOnnxModelManifestSerializer.WriteAtomicallyAsync(
                ManifestPath,
                incomplete,
                TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxModelErrorCodes.ManifestInvalid, exception.ErrorCode);
        Assert.False(File.Exists(ManifestPath));
        Assert.Empty(Directory.EnumerateFiles(directory.FullPath, "*", SearchOption.AllDirectories));
    }

    /// <summary>An unknown kind is refused rather than read as the kind with the fewest rules.</summary>
    [Fact]
    public async Task Read_RefusesAKindItDoesNotKnow()
    {
        await WriteManifestAsync(JudgeManifest(), manifest => SetField(manifest, "kind", "summarizer"));

        var exception = await Assert.ThrowsAsync<LocalOnnxModelStoreException>(() =>
            LocalOnnxModelManifestSerializer.ReadAsync(ManifestPath, TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxModelErrorCodes.ManifestInvalid, exception.ErrorCode);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("kind")]
    [InlineData("id")]
    [InlineData("revision")]
    [InlineData("dimensions")]
    [InlineData("maxTokens")]
    [InlineData("pooling")]
    [InlineData("normalize")]
    [InlineData("queryPrefix")]
    [InlineData("passagePrefix")]
    [InlineData("license")]
    [InlineData("modelFile")]
    [InlineData("modelFile.path")]
    [InlineData("modelFile.url")]
    [InlineData("modelFile.sha256")]
    [InlineData("modelFile.kind")]
    [InlineData("tokenizerFile.url")]
    [InlineData("tokenizerFile.kind")]
    public async Task Read_RefusesAManifestMissingAField(string field)
    {
        await WriteManifestAsync(EmbeddingManifest(), manifest => RemoveField(manifest, field));

        var exception = await Assert.ThrowsAsync<LocalOnnxModelStoreException>(() =>
            LocalOnnxModelManifestSerializer.ReadAsync(ManifestPath, TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxModelErrorCodes.ManifestInvalid, exception.ErrorCode);
    }

    [Theory]
    [InlineData("modelFile.kind", "sentencepiece")]
    [InlineData("tokenizerFile.kind", "onnx")]
    [InlineData("modelFile.url", "http://models.example/org/model/resolve/rev-1/onnx/model.onnx")]
    [InlineData("pooling", "cls")]
    [InlineData("queryPrefix", null)]
    public async Task Read_RefusesAFieldTheAdapterCannotUse(string field, string? value)
    {
        await WriteManifestAsync(EmbeddingManifest(), manifest => SetField(manifest, field, value));

        var exception = await Assert.ThrowsAsync<LocalOnnxModelStoreException>(() =>
            LocalOnnxModelManifestSerializer.ReadAsync(ManifestPath, TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxModelErrorCodes.ManifestInvalid, exception.ErrorCode);
    }

    private string ManifestPath => LocalOnnxModelLayout.GetManifestPath(directory.FullPath);

    private LocalOnnxModelManifest EmbeddingManifest() =>
        LocalOnnxModelStore.CreateManifest(LocalOnnxTestArtifacts.Options(directory.FullPath).CreatePin());

    private LocalOnnxModelManifest JudgeManifest() =>
        LocalOnnxModelStore.CreateManifest(LocalOnnxTestArtifacts.JudgePin(directory.FullPath));

    private async Task WriteManifestAsync(LocalOnnxModelManifest manifest, Action<JsonObject> change)
    {
        await LocalOnnxModelManifestSerializer.WriteAtomicallyAsync(ManifestPath, manifest, TestContext.Current.CancellationToken);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(ManifestPath, TestContext.Current.CancellationToken))!.AsObject();
        change(node);
        await File.WriteAllTextAsync(ManifestPath, node.ToJsonString(), TestContext.Current.CancellationToken);
    }

    private static void RemoveField(JsonObject manifest, string field)
    {
        var (parent, name) = Locate(manifest, field);
        Assert.True(parent.Remove(name), $"The written manifest has no field {field}.");
    }

    private static void SetField(JsonObject manifest, string field, string? value)
    {
        var (parent, name) = Locate(manifest, field);
        Assert.True(parent.ContainsKey(name), $"The written manifest has no field {field}.");
        parent[name] = value is null ? null : JsonValue.Create(value);
    }

    private static (JsonObject Parent, string Name) Locate(JsonObject manifest, string field)
    {
        var segments = field.Split('.');
        var parent = manifest;
        foreach (var segment in segments[..^1])
        {
            parent = parent[segment]!.AsObject();
        }

        return (parent, segments[^1]);
    }
}
