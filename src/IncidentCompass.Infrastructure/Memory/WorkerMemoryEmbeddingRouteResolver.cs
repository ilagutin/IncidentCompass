using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// The embedding route as the Worker builds and judges the memory corpus under it. The seed pass,
/// <c>memory rebuild</c> and the Worker's <c>memory status</c> all resolve through this one type.
/// <para>
/// A route whose provider is the in-process local model is judged against the installed model
/// before anything is embedded. An installed model whose id is not the route's model is a model
/// mismatch, and no usable installed model is an unavailable model; both block the route. Otherwise
/// the route's model becomes the installed model's encoded identity, so a model file replaced under
/// the same id no longer matches the corpus and is reported as the existing route change. Every
/// other route is returned as configured. The Api, which has no model, judges through
/// <see cref="MemoryEmbeddingRouteResolver" /> and <see cref="MemoryCorpusStatusReader" /> instead.
/// </para>
/// <para>
/// On a host whose embedding provider is <c>Mock</c> every route is returned as configured, the local
/// one included. The mock adapter answers any route itself and embeds nothing with a model, so there is
/// no installed model to judge the route against; this is what lets the mock compose overlay and the
/// deterministic tests run the shipped configuration, whose memory route names the local model.
/// </para>
/// </summary>
internal sealed class WorkerMemoryEmbeddingRouteResolver(
    IOptions<EmbeddingOptions> embeddingOptions,
    LocalOnnxInstalledModelReader installedModelReader)
{
    public async Task<MemoryEmbeddingRouteResolution> ResolveAsync(
        TriageConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var route = MemoryEmbeddingRouteResolver.Resolve(configuration);
        if (!MemoryEmbeddingRouteResolver.IsLocalOnnxRoute(configuration, route) || IsMockHost())
        {
            return MemoryEmbeddingRouteResolution.Ready(route);
        }

        var lookup = await installedModelReader.ReadAsync(cancellationToken);
        if (lookup.Model is null)
        {
            return MemoryEmbeddingRouteResolution.Blocked(
                route,
                MemoryCorpusState.EmbeddingModelUnavailable,
                lookup.ErrorCode,
                lookup.Detail ?? "No usable local embedding model is installed.");
        }

        var manifest = lookup.Model.Manifest;
        if (!string.Equals(manifest.Id, route.Model, StringComparison.Ordinal))
        {
            return MemoryEmbeddingRouteResolution.Blocked(
                route,
                MemoryCorpusState.EmbeddingModelMismatch,
                modelErrorCode: null,
                $"The installed local embedding model is '{manifest.Id}', but route '{route.RouteId}' names" +
                $" '{route.Model}'.");
        }

        return MemoryEmbeddingRouteResolution.Ready(route with { Model = LocalOnnxModelIdentity.Describe(manifest) });
    }

    private bool IsMockHost() =>
        ProviderKindParser.TryParse(embeddingOptions.Value.Provider, out var kind) && kind == ProviderKind.Mock;
}
