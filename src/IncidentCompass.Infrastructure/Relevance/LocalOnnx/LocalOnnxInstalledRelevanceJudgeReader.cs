using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// The one place the host asks which local relevance judge is installed, mirroring
/// <c>LocalOnnxInstalledModelReader</c> for the judge's own directory and install state.
/// <para>
/// While a host runs, an install pass has already answered and its recorded state is used. Before
/// one has run, the model store is read and both files verified instead; a verified model is then
/// recorded, so the two files are not hashed again for every scored candidate set. The pinned judge
/// is well over half a gigabyte, so that difference is the whole point of recording it.
/// </para>
/// </summary>
internal sealed class LocalOnnxInstalledRelevanceJudgeReader(
    IOptions<LocalOnnxRelevanceJudgeOptions> options,
    LocalOnnxModelInstallState installState,
    LocalOnnxModelStore store)
{
    public async Task<LocalOnnxInstalledModelLookup> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = installState.Snapshot;
        if (snapshot is { Status: LocalOnnxModelInstallStatus.Installed, Model: not null })
        {
            return LocalOnnxInstalledModelLookup.Found(snapshot.Model);
        }

        if (snapshot.Status == LocalOnnxModelInstallStatus.Failed)
        {
            return LocalOnnxInstalledModelLookup.NotAvailable(
                snapshot.ErrorCode ?? LocalOnnxModelErrorCodes.NotInstalled,
                snapshot.Detail ?? "The local relevance judge install failed.");
        }

        if (snapshot.Status == LocalOnnxModelInstallStatus.Installing)
        {
            return LocalOnnxInstalledModelLookup.NotAvailable(
                LocalOnnxModelErrorCodes.NotInstalled,
                "The local relevance judge install has not finished.");
        }

        return await ReadFromStoreAsync(cancellationToken);
    }

    private async Task<LocalOnnxInstalledModelLookup> ReadFromStoreAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.ModelDirectory))
        {
            return LocalOnnxInstalledModelLookup.NotAvailable(
                LocalOnnxModelErrorCodes.StoreUnavailable,
                $"{LocalOnnxRelevanceJudgeOptions.SectionName}:ModelDirectory is not set.");
        }

        try
        {
            var installed = await store.ReadInstalledAsync(configured.CreatePin(), cancellationToken);
            if (installed is null)
            {
                return LocalOnnxInstalledModelLookup.NotAvailable(
                    LocalOnnxModelErrorCodes.NotInstalled,
                    "No local relevance judge is installed in its model directory.");
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
