using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// A triage configuration whose memory route is served by the local model, install states for a
/// manifest with a chosen id and model digest, and the installed-model reader over them.
/// </summary>
internal static class LocalModelTestSupport
{
    public const string ModelId = "intfloat/multilingual-e5-small";
    public const string LocalProviderId = "local-embed";
    public const string RouteId = "memory-embed";

    public static readonly string FirstDigest = new('a', 64);
    public static readonly string SecondDigest = new('b', 64);

    public static TriageConfiguration Configuration(string routeModel = ModelId)
    {
        var configuration = TestTriageConfiguration.Create();
        return configuration with
        {
            Providers = new Dictionary<string, TriageProviderSettings>(configuration.Providers, StringComparer.Ordinal)
            {
                [LocalProviderId] = new("LocalOnnx", Endpoint: null, ApiKeySecretRef: null)
            },
            Routes = new Dictionary<string, TriageRouteSettings>(configuration.Routes, StringComparer.Ordinal)
            {
                [RouteId] = configuration.Routes[RouteId] with { ProviderId = LocalProviderId, Model = routeModel }
            }
        };
    }

    public static LocalOnnxInstalledModel InstalledModel(string modelId, string modelDigest)
    {
        var options = new LocalOnnxEmbeddingOptions
        {
            ModelDirectory = Path.GetTempPath(),
            ModelId = modelId,
            ModelFileSha256 = modelDigest
        };
        return new LocalOnnxInstalledModel(LocalOnnxModelStore.CreateManifest(options), "unused.onnx", "unused.model");
    }

    public static LocalOnnxModelInstallState InstalledState(string modelId, string modelDigest)
    {
        var state = new LocalOnnxModelInstallState();
        state.RecordInstalled(InstalledModel(modelId, modelDigest));
        return state;
    }

    public static LocalOnnxModelInstallState FailedState(string errorCode)
    {
        var state = new LocalOnnxModelInstallState();
        state.RecordFailed(errorCode, "The local embedding model install failed in this test.");
        return state;
    }

    public static LocalOnnxInstalledModelReader Reader(
        LocalOnnxModelInstallState state,
        string? modelDirectory = null,
        string provider = "LocalOnnx") =>
        new(
            Options.Create(new EmbeddingOptions { Provider = provider }),
            Options.Create(new LocalOnnxEmbeddingOptions { ModelDirectory = modelDirectory ?? Path.GetTempPath() }),
            state,
            LocalOnnxTestArtifacts.Store(ScriptedHttpMessageHandler.Refusing()));

    public static string Encoded(string modelDigest, string modelId = ModelId) =>
        EncodedEmbeddingModelIdentity.Encode(modelId, modelDigest);

    public static MemoryCorpusInventory Corpus(string embeddingModel, Guid generation)
    {
        var identity = new MemoryCorpusIdentity(RouteId, LocalProviderId, "local-onnx", embeddingModel, 8);
        return new MemoryCorpusInventory(
            new MemoryCorpusGeneration(generation, identity, 1, 1, DateTimeOffset.UnixEpoch),
            [identity],
            1,
            1);
    }
}
