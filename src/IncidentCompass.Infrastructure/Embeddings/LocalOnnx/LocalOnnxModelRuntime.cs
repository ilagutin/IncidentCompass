using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

/// <summary>
/// Holds the one loaded model of this process and runs embedding calls on it, one at a time.
/// <para>
/// The session and the tokenizer are loaded once, on the first call, from the installed model the
/// caller passes, and reloaded only if a different installed model is passed. Calls are serialized
/// by a single-slot semaphore. The session runs on
/// <see cref="LocalOnnxEmbeddingOptions.IntraOpThreads" /> threads, so running two calls at once
/// would only trade one core for two without shortening either, and serializing makes the
/// tokenizer's own thread-safety irrelevant rather than something to rely on. A waiting call
/// honours its cancellation token.
/// </para>
/// </summary>
internal sealed class LocalOnnxModelRuntime(IOptions<LocalOnnxEmbeddingOptions> options) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private LocalOnnxLoadedModel? loadedModel;

    public async Task<(float[] Vector, int InputTokens)> EmbedAsync(
        LocalOnnxInstalledModel installed,
        string input,
        EmbeddingInputKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installed);
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetOrLoad(installed).Embed(input, kind, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        loadedModel?.Dispose();
        gate.Dispose();
    }

    internal int CountPassageTokens(LocalOnnxInstalledModel installed, string text, CancellationToken cancellationToken) =>
        CountTokens(installed, text, includePassageFraming: true, cancellationToken);

    internal int CountOverlapTokens(LocalOnnxInstalledModel installed, string text, CancellationToken cancellationToken) =>
        CountTokens(installed, text, includePassageFraming: false, cancellationToken);

    private int CountTokens(
        LocalOnnxInstalledModel installed, string text, bool includePassageFraming, CancellationToken cancellationToken)
    {
        gate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var encoder = GetOrLoad(installed).Encoder;
            return includePassageFraming
                ? LocalOnnxChunkTokenCounter.CountTokens(encoder, text)
                : LocalOnnxChunkTokenCounter.CountOverlapTokens(encoder, text);
        }
        finally
        {
            gate.Release();
        }
    }

    private LocalOnnxLoadedModel GetOrLoad(LocalOnnxInstalledModel installed)
    {
        if (loadedModel is not null && loadedModel.Installed == installed)
        {
            return loadedModel;
        }

        loadedModel?.Dispose();
        loadedModel = null;
        loadedModel = LocalOnnxLoadedModel.Load(installed, options.Value.IntraOpThreads);
        return loadedModel;
    }
}
