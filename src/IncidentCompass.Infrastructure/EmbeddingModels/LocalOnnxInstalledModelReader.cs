using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// The one place the Worker asks which local embedding model is installed.
/// <para>
/// While the host runs, the install pass has already answered at start and its recorded state is
/// used. Before the host starts, which is when an operator command such as <c>memory rebuild</c>
/// runs, no install pass has run, so the model store is read and both files verified instead; a
/// verified model is then recorded, so the command does not verify it again for every embedding call.
/// A host whose embedding provider is not <c>LocalOnnx</c> has no local model.
/// </para>
/// </summary>
internal sealed class LocalOnnxInstalledModelReader(
    IOptions<EmbeddingOptions> embeddingOptions,
    IOptions<LocalOnnxEmbeddingOptions> localOnnxOptions,
    LocalOnnxModelInstallState installState,
    LocalOnnxModelStore store)
{
    public async Task<LocalOnnxInstalledModelLookup> ReadAsync(CancellationToken cancellationToken)
    {
        var provider = embeddingOptions.Value.Provider;
        if (!ProviderKindParser.IsLocalOnnx(provider))
        {
            return LocalOnnxInstalledModelLookup.NotAvailable(
                LocalOnnxModelErrorCodes.NotInstalled,
                $"The host embedding provider is '{provider}', not LocalOnnx, so this host has no local embedding model.");
        }

        var snapshot = installState.Snapshot;
        if (snapshot is { Status: LocalOnnxModelInstallStatus.Installed, Model: not null })
        {
            return LocalOnnxInstalledModelLookup.Found(snapshot.Model);
        }

        if (snapshot.Status == LocalOnnxModelInstallStatus.Failed)
        {
            return LocalOnnxInstalledModelLookup.NotAvailable(
                snapshot.ErrorCode ?? LocalOnnxModelErrorCodes.NotInstalled,
                snapshot.Detail ?? "The local embedding model install failed.");
        }

        if (snapshot.Status == LocalOnnxModelInstallStatus.Installing)
        {
            return LocalOnnxInstalledModelLookup.NotAvailable(
                LocalOnnxModelErrorCodes.NotInstalled,
                "The local embedding model install has not finished.");
        }

        return await ReadFromStoreAsync(cancellationToken);
    }

    private async Task<LocalOnnxInstalledModelLookup> ReadFromStoreAsync(CancellationToken cancellationToken)
    {
        var options = localOnnxOptions.Value;
        if (string.IsNullOrWhiteSpace(options.ModelDirectory))
        {
            return LocalOnnxInstalledModelLookup.NotAvailable(
                LocalOnnxModelErrorCodes.StoreUnavailable,
                $"{LocalOnnxEmbeddingOptions.SectionName}:ModelDirectory is not set.");
        }

        try
        {
            var installed = await store.ReadInstalledAsync(options.CreatePin(), cancellationToken);
            if (installed is null)
            {
                return LocalOnnxInstalledModelLookup.NotAvailable(
                    LocalOnnxModelErrorCodes.NotInstalled,
                    "No local embedding model is installed in the model directory; run 'memory model install'.");
            }

            installState.RecordInstalled(installed);
            return LocalOnnxInstalledModelLookup.Found(installed);
        }
        catch (LocalOnnxModelStoreException exception)
        {
            return LocalOnnxInstalledModelLookup.NotAvailable(exception.ErrorCode, exception.Message);
        }
    }
}
