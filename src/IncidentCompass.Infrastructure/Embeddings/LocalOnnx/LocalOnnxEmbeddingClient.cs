using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

/// <summary>
/// The in-process embedding adapter the <c>LocalOnnx</c> provider kind selects. It runs the installed
/// model, and refuses a call with a named code when it cannot answer it honestly: an undefined input
/// kind, a route provider that is not a <c>LocalOnnx</c> provider, a model name other than the
/// installed one, or no usable installed model. A refused call never falls back to another model.
/// Every vector it returns names the encoded identity of the model file that produced it.
/// </summary>
internal sealed class LocalOnnxEmbeddingClient(
    LocalOnnxInstalledModelReader installedModelReader,
    LocalOnnxModelRuntime runtime,
    ITriageConfigurationRepository configurationRepository) : IEmbeddingClient
{
    public async Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (!Enum.IsDefined(request.Kind))
        {
            throw LocalOnnxEmbeddingErrors.InputKindInvalid(request.Kind);
        }

        await EnsureRouteProviderIsLocalAsync(request.ProviderId, cancellationToken);
        var lookup = await installedModelReader.ReadAsync(cancellationToken);
        var installed = lookup.Model ?? throw LocalOnnxEmbeddingErrors.NotAvailable(lookup);
        if (!LocalOnnxModelIdentity.Matches(request.Model, installed.Manifest))
        {
            throw LocalOnnxEmbeddingErrors.ModelMismatch(request.Model, installed.Manifest.Id);
        }

        var (vector, inputTokens) = await runtime.EmbedAsync(installed, request.Input, request.Kind, cancellationToken);
        return new EmbeddingResponse(
            vector,
            LocalOnnxModelIdentity.Describe(installed.Manifest),
            LocalOnnxEmbeddingProvider.Name,
            inputTokens,
            request.CorrelationId);
    }

    /// <summary>
    /// A route that names a provider must name a <c>LocalOnnx</c> one. A host adapter and a route
    /// provider that disagree fail at the first call with a message naming both, as the
    /// OpenAI-compatible adapter does for the opposite mismatch. A blank provider id is a direct
    /// caller outside the governed routes and reads no configuration.
    /// </summary>
    private async Task EnsureRouteProviderIsLocalAsync(string? providerId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }

        TriageConfiguration configuration;
        try
        {
            configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw LocalOnnxEmbeddingErrors.ConfigurationReadFailed(exception);
        }

        if (!configuration.Providers.TryGetValue(providerId, out var provider))
        {
            throw LocalOnnxEmbeddingErrors.RouteProviderMismatch(
                $"Route provider '{providerId}' names no entry under Providers.");
        }

        if (!string.Equals(provider.Kind, ProviderKindHostDefaultRule.LocalOnnxKind, StringComparison.Ordinal))
        {
            throw LocalOnnxEmbeddingErrors.RouteProviderMismatch(
                $"Route provider '{providerId}' has Kind '{provider.Kind}', but the host embedding provider is" +
                $" {ProviderKindHostDefaultRule.LocalOnnxKind}, which serves only providers of Kind" +
                $" '{ProviderKindHostDefaultRule.LocalOnnxKind}'.");
        }
    }
}
