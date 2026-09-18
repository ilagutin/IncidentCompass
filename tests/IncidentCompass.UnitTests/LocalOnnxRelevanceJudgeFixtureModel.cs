using System.Text.Json;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The committed relevance-judge fixture from <c>tests/Shared/fixtures/relevance-judge-model/</c>,
/// installed into a temporary model directory through the store's pre-placed-file path. Judge
/// adapter tests therefore load what the store verified, exactly as production does, with no
/// download.
/// </summary>
internal sealed class LocalOnnxRelevanceJudgeFixtureModel : IDisposable
{
    /// <summary>
    /// A graph in the fixture directory whose output is named <c>logits</c> but carries one score per
    /// token. It is never installed through a manifest; a test points an installed model's file path
    /// at it to exercise the output-shape refusal.
    /// </summary>
    public const string WrongShapeModelFileName = "wrong-shape-model.onnx";

    private readonly LocalOnnxTestDirectory directory;

    private LocalOnnxRelevanceJudgeFixtureModel(
        LocalOnnxTestDirectory directory,
        LocalOnnxModelManifest fixtureManifest,
        LocalOnnxRelevanceJudgeOptions options,
        LocalOnnxInstalledModel installed)
    {
        this.directory = directory;
        FixtureManifest = fixtureManifest;
        Options = options;
        Installed = installed;
    }

    public static string FixtureDirectory =>
        Path.Combine(RepositoryRootLocator.Find(), "tests", "Shared", "fixtures", "relevance-judge-model");

    public LocalOnnxModelManifest FixtureManifest { get; }

    public LocalOnnxRelevanceJudgeOptions Options { get; }

    public LocalOnnxInstalledModel Installed { get; }

    public static string WrongShapeModelPath => Path.Combine(FixtureDirectory, WrongShapeModelFileName);

    public static async Task<LocalOnnxModelManifest> ReadFixtureManifestAsync(CancellationToken cancellationToken) =>
        await LocalOnnxModelManifestSerializer.ReadAsync(
            Path.Combine(FixtureDirectory, LocalOnnxModelLayout.ManifestFileName),
            cancellationToken)
        ?? throw new InvalidOperationException("The relevance judge fixture manifest is missing.");

    public static IReadOnlyList<LocalOnnxRelevanceJudgeFixtureCase> ReadExpectedCases()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(FixtureDirectory, "expected-scores.json")));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(static element => new LocalOnnxRelevanceJudgeFixtureCase(
                element.GetProperty("query").GetString()!,
                element.GetProperty("passage").GetString()!,
                element.GetProperty("ids").EnumerateArray().Select(static id => id.GetInt64()).ToArray(),
                element.GetProperty("score").GetSingle()))
            .ToArray();
    }

    public static async Task<LocalOnnxRelevanceJudgeFixtureModel> InstallAsync(CancellationToken cancellationToken)
    {
        var fixtureManifest = await ReadFixtureManifestAsync(cancellationToken);
        var directory = new LocalOnnxTestDirectory();
        try
        {
            var options = OptionsFor(fixtureManifest, directory.FullPath);
            PlaceFixtureFiles(fixtureManifest, directory.FullPath);
            using var handler = ScriptedHttpMessageHandler.Refusing();
            var installed = await LocalOnnxTestArtifacts.Store(handler)
                .EnsureInstalledAsync(options.CreatePin(), cancellationToken);
            return new LocalOnnxRelevanceJudgeFixtureModel(directory, fixtureManifest, options, installed);
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    public static LocalOnnxRelevanceJudgeOptions OptionsFor(
        LocalOnnxModelManifest manifest,
        string modelDirectory,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new LocalOnnxRelevanceJudgeOptions
        {
            ModelDirectory = modelDirectory,
            ModelId = modelId ?? manifest.Id,
            Revision = manifest.Revision,
            ModelFileUrl = manifest.ModelFile.Url,
            ModelFileSha256 = manifest.ModelFile.Sha256,
            TokenizerFileUrl = manifest.TokenizerFile.Url,
            TokenizerFileSha256 = manifest.TokenizerFile.Sha256,
            MaxTokens = manifest.MaxTokens,
            License = manifest.License
        };
    }

    public static LocalOnnxPairEncoder LoadFixtureEncoder(LocalOnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return LocalOnnxPairEncoder.Load(
            Path.Combine(FixtureDirectory, manifest.TokenizerFile.Path),
            manifest);
    }

    public void Dispose() => directory.Dispose();

    /// <summary>
    /// Places both fixture files where the store expects them in <paramref name="modelDirectory" />,
    /// without writing a manifest, as an operator installing offline would.
    /// </summary>
    public static void PlaceFixtureFiles(LocalOnnxModelManifest fixtureManifest, string modelDirectory)
    {
        ArgumentNullException.ThrowIfNull(fixtureManifest);
        PlaceFixtureFile(fixtureManifest.ModelFile, modelDirectory);
        PlaceFixtureFile(fixtureManifest.TokenizerFile, modelDirectory);
    }

    private static void PlaceFixtureFile(LocalOnnxModelArtifact artifact, string modelDirectory)
    {
        var destination = Path.Combine(
            modelDirectory,
            LocalOnnxModelLayout.GetArtifactRelativePath(artifact.Sha256, artifact.Url));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(Path.Combine(FixtureDirectory, artifact.Path), destination);
    }
}
