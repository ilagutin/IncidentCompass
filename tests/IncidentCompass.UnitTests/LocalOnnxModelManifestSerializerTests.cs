using System.Text.Json.Nodes;
using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Every manifest field is required. A manifest missing one, or carrying a value the adapter cannot
/// use, is refused with the manifest code instead of being read with a default: a missing
/// <c>normalize</c> would otherwise read as false and produce unnormalized vectors.
/// </summary>
public sealed class LocalOnnxModelManifestSerializerTests : IDisposable
{
    private readonly LocalOnnxTestDirectory directory = new();

    public void Dispose() => directory.Dispose();

    [Fact]
    public async Task WriteThenRead_RoundTripsTheManifest()
    {
        var manifest = LocalOnnxModelStore.CreateManifest(LocalOnnxTestArtifacts.Options(directory.FullPath));

        await LocalOnnxModelManifestSerializer.WriteAtomicallyAsync(ManifestPath, manifest, TestContext.Current.CancellationToken);

        Assert.Equal(manifest, await LocalOnnxModelManifestSerializer.ReadAsync(ManifestPath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("schemaVersion")]
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
        await WriteManifestAsync(manifest => RemoveField(manifest, field));

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
        await WriteManifestAsync(manifest => SetField(manifest, field, value));

        var exception = await Assert.ThrowsAsync<LocalOnnxModelStoreException>(() =>
            LocalOnnxModelManifestSerializer.ReadAsync(ManifestPath, TestContext.Current.CancellationToken));

        Assert.Equal(LocalOnnxModelErrorCodes.ManifestInvalid, exception.ErrorCode);
    }

    private string ManifestPath => LocalOnnxModelLayout.GetManifestPath(directory.FullPath);

    private async Task WriteManifestAsync(Action<JsonObject> change)
    {
        var manifest = LocalOnnxModelStore.CreateManifest(LocalOnnxTestArtifacts.Options(directory.FullPath));
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
