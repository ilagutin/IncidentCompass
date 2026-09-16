using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Memory;

namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

/// <summary>Counts the uncapped passage on the adapter's tokenizer, serialized with inference.</summary>
internal sealed class LocalOnnxChunkTokenCounter(
    LocalOnnxInstalledModelReader installedModelReader,
    LocalOnnxModelRuntime runtime) : IMemoryChunkTokenCounter
{
    private LocalOnnxInstalledModel? installed;
    private CancellationToken countingCancellation;

    public string Kind => "exact";

    public async Task InitializeAsync(MemoryChunkingOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        countingCancellation = cancellationToken;
        var lookup = await installedModelReader.ReadAsync(cancellationToken);
        installed = lookup.Model;
        if (installed is not null)
        {
            var prefixAndMarkers = runtime.CountPassageTokens(installed, string.Empty, cancellationToken);
            ValidateWindow(options, installed.Manifest.MaxTokens, prefixAndMarkers);
        }
    }

    public int CountTokens(string text) => runtime.CountPassageTokens(
        installed ?? throw new InvalidOperationException("The memory chunk tokenizer is not available."), text, countingCancellation);

    public int CountOverlapTokens(string text) => runtime.CountOverlapTokens(
        installed ?? throw new InvalidOperationException("The memory chunk tokenizer is not available."), text, countingCancellation);

    internal static int CountTokens(LocalOnnxInputEncoder encoder, string text) =>
        encoder.Tokenizer.EncodeToIds(
            encoder.PassagePrefix + text.Trim(),
            addBeginningOfSentence: false,
            addEndOfSentence: false).Count + 2;

    internal static int CountOverlapTokens(LocalOnnxInputEncoder encoder, string text) =>
        encoder.Tokenizer.EncodeToIds(
            text.Trim(),
            addBeginningOfSentence: false,
            addEndOfSentence: false).Count;

    internal static void ValidateWindow(MemoryChunkingOptions options, int modelMaxTokens, int prefixAndMarkers)
    {
        if ((long)options.MaxTokens + prefixAndMarkers > modelMaxTokens)
        {
            throw new InvalidOperationException(
                "Memory chunking MaxTokens plus the passage prefix and two sequence markers exceeds the installed model window.");
        }
    }
}
