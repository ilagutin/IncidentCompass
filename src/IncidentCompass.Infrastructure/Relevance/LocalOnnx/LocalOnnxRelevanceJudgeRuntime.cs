using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// Holds the one loaded relevance judge of this process and scores candidate sets on it, one set at
/// a time.
/// <para>
/// The session and the tokenizer are loaded once, on the first call, from the installed model the
/// caller passes, and reloaded only if a different installed model is passed. Calls are serialized
/// by a single-slot semaphore, for the reasons the embedding runtime serializes its own: the session
/// already runs on <see cref="LocalOnnxRelevanceJudgeOptions.IntraOpThreads" /> threads, and
/// serializing makes the tokenizer's own thread-safety irrelevant rather than something to rely on.
/// A waiting call honours its cancellation token.
/// </para>
/// </summary>
internal sealed class LocalOnnxRelevanceJudgeRuntime(IOptions<LocalOnnxRelevanceJudgeOptions> options) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private LocalOnnxLoadedRelevanceJudge? loadedModel;

    /// <summary>
    /// One score per candidate, in the candidates' own order. The token is checked before each pair
    /// as well as inside the run, so a cancelled call stops promptly on a long candidate set instead
    /// of scoring the rest of it first.
    /// </summary>
    public async Task<float[]> ScoreAsync(
        LocalOnnxInstalledModel installed,
        string query,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(candidates);
        await gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var judge = GetOrLoad(installed);
            var scores = new float[candidates.Count];
            for (var index = 0; index < scores.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scores[index] = judge.Score(query, candidates[index], cancellationToken);
            }

            return scores;
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

    private LocalOnnxLoadedRelevanceJudge GetOrLoad(LocalOnnxInstalledModel installed)
    {
        if (loadedModel is not null && loadedModel.Installed == installed)
        {
            return loadedModel;
        }

        loadedModel?.Dispose();
        loadedModel = null;
        loadedModel = LocalOnnxLoadedRelevanceJudge.Load(installed, options.Value.IntraOpThreads);
        return loadedModel;
    }
}
