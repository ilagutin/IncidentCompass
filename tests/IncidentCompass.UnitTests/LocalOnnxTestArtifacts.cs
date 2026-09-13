using System.Security.Cryptography;
using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Two small byte arrays standing in for a model file and a tokenizer file, the options that pin
/// them, and a handler that serves them. The store never looks inside a file, so the bytes are
/// arbitrary; only their digests matter.
/// </summary>
internal static class LocalOnnxTestArtifacts
{
    public const string ModelUrl = "https://models.example/org/model/resolve/rev-1/onnx/model.onnx";
    public const string TokenizerUrl = "https://models.example/org/model/resolve/rev-1/onnx/sentencepiece.bpe.model";

    public static byte[] ModelBytes { get; } = CreateBytes(4096, seed: 1);

    public static byte[] TokenizerBytes { get; } = CreateBytes(1024, seed: 2);

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static LocalOnnxEmbeddingOptions Options(
        string modelDirectory,
        long maxDownloadBytes = LocalOnnxEmbeddingOptions.DefaultMaxDownloadBytes)
    {
        return new LocalOnnxEmbeddingOptions
        {
            ModelDirectory = modelDirectory,
            ModelId = "test/model",
            Revision = "rev-1",
            ModelFileUrl = ModelUrl,
            ModelFileSha256 = Sha256(ModelBytes),
            TokenizerFileUrl = TokenizerUrl,
            TokenizerFileSha256 = Sha256(TokenizerBytes),
            Dimensions = 8,
            MaxTokens = 16,
            License = "Apache-2.0",
            MaxDownloadBytes = maxDownloadBytes
        };
    }

    public static ScriptedHttpMessageHandler ServingBoth() =>
        ScriptedHttpMessageHandler.Serving(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [ModelUrl] = ModelBytes,
            [TokenizerUrl] = TokenizerBytes
        });

    public static LocalOnnxModelStore Store(HttpMessageHandler handler) =>
        new(new LocalOnnxModelFileFetcher(new HttpClient(handler)));

    public static byte[] CreateBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
