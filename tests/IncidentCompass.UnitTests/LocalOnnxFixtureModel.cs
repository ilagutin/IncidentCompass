using System.Text.Json;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The committed fixture model from <c>tests/Shared/fixtures/embedding-model/</c>, installed into a
/// temporary model directory through the store's pre-placed-file path. Adapter tests therefore load
/// what the store verified, exactly as production does, with no download.
/// </summary>
internal sealed class LocalOnnxFixtureModel : IDisposable
{
    private readonly LocalOnnxTestDirectory directory;

    private LocalOnnxFixtureModel(
        LocalOnnxTestDirectory directory,
        LocalOnnxModelManifest fixtureManifest,
        LocalOnnxEmbeddingOptions options,
        LocalOnnxInstalledModel installed)
    {
        this.directory = directory;
        FixtureManifest = fixtureManifest;
        Options = options;
        Installed = installed;
    }

    public static string FixtureDirectory =>
        Path.Combine(RepositoryRootLocator.Find(), "tests", "Shared", "fixtures", "embedding-model");

    public LocalOnnxModelManifest FixtureManifest { get; }

    public LocalOnnxEmbeddingOptions Options { get; }

    public LocalOnnxInstalledModel Installed { get; }

    public static async Task<LocalOnnxModelManifest> ReadFixtureManifestAsync(CancellationToken cancellationToken) =>
        await LocalOnnxModelManifestSerializer.ReadAsync(
            Path.Combine(FixtureDirectory, LocalOnnxModelLayout.ManifestFileName),
            cancellationToken)
        ?? throw new InvalidOperationException("The fixture manifest is missing.");

    public static IReadOnlyList<LocalOnnxFixtureCase> ReadExpectedCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "expected-ids.json")));
        return document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(static element => new LocalOnnxFixtureCase(
                Enum.Parse<EmbeddingInputKind>(element.GetProperty("kind").GetString()!),
                element.GetProperty("input").GetString()!,
                element.GetProperty("ids").EnumerateArray().Select(static id => id.GetInt64()).ToArray()))
            .ToArray();
    }

    public static async Task<LocalOnnxFixtureModel> InstallAsync(CancellationToken cancellationToken)
    {
        var fixtureManifest = await ReadFixtureManifestAsync(cancellationToken);
        var directory = new LocalOnnxTestDirectory();
        try
        {
            var options = OptionsFor(fixtureManifest, directory.FullPath);
            PlaceFixtureFiles(fixtureManifest, directory.FullPath);
            using var handler = ScriptedHttpMessageHandler.Refusing();
            var installed = await LocalOnnxTestArtifacts.Store(handler).EnsureInstalledAsync(options.CreatePin(), cancellationToken);
            return new LocalOnnxFixtureModel(directory, fixtureManifest, options, installed);
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    public static LocalOnnxEmbeddingOptions OptionsFor(
        LocalOnnxModelManifest manifest,
        string modelDirectory,
        int installTimeoutSeconds = 900)
    {
        var profile = manifest.GetEmbeddingProfile();
        return new LocalOnnxEmbeddingOptions
        {
            InstallTimeoutSeconds = installTimeoutSeconds,
            ModelDirectory = modelDirectory,
            ModelId = manifest.Id,
            Revision = manifest.Revision,
            ModelFileUrl = manifest.ModelFile.Url,
            ModelFileSha256 = manifest.ModelFile.Sha256,
            TokenizerFileUrl = manifest.TokenizerFile.Url,
            TokenizerFileSha256 = manifest.TokenizerFile.Sha256,
            Dimensions = profile.Dimensions,
            MaxTokens = manifest.MaxTokens,
            QueryPrefix = profile.QueryPrefix,
            PassagePrefix = profile.PassagePrefix,
            Pooling = profile.Pooling,
            Normalize = profile.Normalize,
            License = manifest.License
        };
    }

    public void Dispose() => directory.Dispose();

    /// <summary>
    /// Places both fixture files where the store expects them in <paramref name="modelDirectory" />,
    /// without writing a manifest, as an operator installing offline would.
    /// </summary>
    public static void PlaceFixtureFiles(LocalOnnxModelManifest fixtureManifest, string modelDirectory)
    {
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
