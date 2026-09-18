using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// The in-process relevance-judge adapter. It runs the installed cross-encoder and refuses a call
/// with a named code when it cannot answer it honestly: no usable installed judge, or an installed
/// model that is not the configured judge. A refused call never falls back to another model and
/// never returns a guessed score.
/// </summary>
internal sealed class LocalOnnxRelevanceJudgeClient(
    LocalOnnxInstalledRelevanceJudgeReader installedModelReader,
    LocalOnnxRelevanceJudgeRuntime runtime,
    IOptions<LocalOnnxRelevanceJudgeOptions> options) : IMemoryRelevanceJudge
{
    public async Task<IReadOnlyList<float>> ScoreAsync(
        string query,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(candidates);

        // Nothing to rank, so nothing is read, verified or loaded. A caller whose retrieval returned
        // no candidate must not pay for a model load, and must not be refused for a judge it was
        // never going to use.
        if (candidates.Count == 0)
        {
            return [];
        }

        var lookup = await installedModelReader.ReadAsync(cancellationToken);
        var installed = lookup.Model ?? throw LocalOnnxRelevanceJudgeErrors.NotAvailable(lookup);
        var manifest = installed.Manifest;
        if (!string.Equals(manifest.Kind, LocalOnnxModelManifest.RelevanceJudgeKind, StringComparison.Ordinal))
        {
            throw LocalOnnxRelevanceJudgeErrors.ModelKindMismatch(manifest.Id, manifest.Kind);
        }

        var configuredModelId = options.Value.ModelId;
        if (!string.Equals(manifest.Id, configuredModelId, StringComparison.Ordinal))
        {
            throw LocalOnnxRelevanceJudgeErrors.ModelMismatch(configuredModelId, manifest.Id);
        }

        return await runtime.ScoreAsync(installed, query, candidates, cancellationToken);
    }
}
