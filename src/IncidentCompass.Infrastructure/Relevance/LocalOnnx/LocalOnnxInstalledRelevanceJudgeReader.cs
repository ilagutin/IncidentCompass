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
/// <para>
/// Every code this reader returns is one of the judge's own, never one of the shared model store's:
/// a store code is spelled for the embedding model and would name the wrong model on a judge
/// surface. The recorded install state already holds judge codes, because the install pass
/// translates them before recording; the codes this reader derives from the store itself are
/// translated here.
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
                snapshot.ErrorCode ?? LocalOnnxRelevanceJudgeProvider.ModelNotInstalledErrorCode,
                snapshot.Detail ?? "The local relevance judge install failed.");
        }

        if (snapshot.Status == LocalOnnxModelInstallStatus.Installing)
        {
            // Its own code, not the not-installed one: this host is installing a judge, so it means
            // to have one, and answering the Application with an absence code would make it admit on
            // the lexical gate alone for the whole download window.
            return LocalOnnxInstalledModelLookup.NotAvailable(
                LocalOnnxRelevanceJudgeProvider.InstallInProgressErrorCode,
                "The local relevance judge install has not finished.");
        }

        return await ReadFromStoreAsync(cancellationToken);
    }

    private async Task<LocalOnnxInstalledModelLookup> ReadFromStoreAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.ModelDirectory))
        {
            // Not an unavailable store: nothing is wrong with the store, this host simply runs no
            // judge. Calling it a store failure sends an operator to look at a volume.
            return LocalOnnxInstalledModelLookup.NotAvailable(
                LocalOnnxRelevanceJudgeProvider.NotConfiguredErrorCode,
                $"{LocalOnnxRelevanceJudgeOptions.SectionName}:ModelDirectory is not set, so this host runs no relevance judge.");
        }

        try
        {
            var installed = await store.ReadInstalledAsync(configured.CreatePin(), cancellationToken);
            if (installed is null)
            {
                return LocalOnnxInstalledModelLookup.NotAvailable(
                    LocalOnnxRelevanceJudgeProvider.ModelNotInstalledErrorCode,
                    "No local relevance judge is installed in its model directory.");
            }

            installState.RecordInstalled(installed);
            return LocalOnnxInstalledModelLookup.Found(installed);
        }
        catch (LocalOnnxModelStoreException exception)
        {
            return LocalOnnxInstalledModelLookup.NotAvailable(
                LocalOnnxRelevanceJudgeStoreErrorCodeMap.Map(exception.ErrorCode),
                exception.Message);
        }
    }
}
