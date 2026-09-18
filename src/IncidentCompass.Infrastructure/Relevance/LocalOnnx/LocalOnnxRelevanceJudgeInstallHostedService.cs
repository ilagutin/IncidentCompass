using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// Installs or verifies the local relevance judge while the Worker starts, on a host that configures
/// a judge model directory. A host that configures none runs no judge and this pass does nothing.
/// <para>
/// The work runs inside <see cref="StartAsync" />, as the embedding model's install pass does, and
/// this one is registered after it and after the memory seed pass: the seed pass must still find the
/// embedding model installed or find its named failure, and nothing on the seed path scores
/// relevance. It is still registered before the claim loop, so a Worker that has started has either
/// verified its judge or recorded why it could not.
/// </para>
/// <para>
/// A failed install does not fail this pass or the host. The failure is recorded with its code in
/// <see cref="LocalOnnxRelevanceJudgeInstallState" /> and every judge call is refused with that code
/// while the host goes on starting. The pass is bounded by
/// <see cref="LocalOnnxRelevanceJudgeOptions.InstallTimeoutSeconds" /> and stops with the host.
/// </para>
/// <para>
/// What it writes out is the same class of value the model store already logs: model ids, revisions,
/// digest prefixes and error codes. No query, candidate or score reaches this pass at all.
/// </para>
/// </summary>
internal sealed partial class LocalOnnxRelevanceJudgeInstallHostedService(
    IOptions<LocalOnnxRelevanceJudgeOptions> options,
    LocalOnnxModelStore store,
    LocalOnnxRelevanceJudgeInstallState installState,
    ILogger<LocalOnnxRelevanceJudgeInstallHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.ModelDirectory))
        {
            // Said out loud, at Information, because it is otherwise invisible: nothing is recorded,
            // nothing fails, and the only other way to learn that this Worker runs no judge is to run
            // 'memory model status' by hand. The setting is named so the log line is the whole fix.
            LogNotConfigured(logger, LocalOnnxRelevanceJudgeOptions.SectionName + ":ModelDirectory");
            return;
        }

        installState.RecordInstalling();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(configured.InstallTimeoutSeconds));
        using var installCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var installed = await store.EnsureInstalledAsync(configured.CreatePin(), installCancellation.Token);
            installState.RecordInstalled(installed);
            LogInstalled(
                logger,
                installed.Manifest.Id,
                installed.Manifest.Revision,
                installed.Manifest.ModelFile.Sha256[..16]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            installState.RecordFailed(
                LocalOnnxRelevanceJudgeProvider.ModelNotInstalledErrorCode,
                "The host stopped before the local relevance judge install completed.");
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            RecordFailure(
                LocalOnnxRelevanceJudgeProvider.InstallTimedOutErrorCode,
                $"The local relevance judge install did not complete within {configured.InstallTimeoutSeconds} seconds.");
        }
        catch (LocalOnnxModelStoreException exception)
        {
            // Translated here, so that neither the recorded state nor the log line names the store's
            // embedding-model vocabulary on a judge path.
            RecordFailure(LocalOnnxRelevanceJudgeStoreErrorCodeMap.Map(exception.ErrorCode), exception.Message);
        }
        catch (Exception exception)
        {
            // Anything else is still a recorded install failure rather than an exception out of this
            // pass, so the Worker keeps starting with the judge path closed.
            RecordFailure(
                LocalOnnxRelevanceJudgeProvider.StoreUnavailableErrorCode,
                $"The local relevance judge install failed with {exception.GetType().Name}.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RecordFailure(string errorCode, string detail)
    {
        installState.RecordFailed(errorCode, detail);
        LogInstallFailed(logger, errorCode, detail);
    }

    [LoggerMessage(
        2320,
        LogLevel.Information,
        "Local relevance judge {ModelId} at revision {Revision} is installed and verified (model file sha256 {ModelDigestPrefix}).")]
    private static partial void LogInstalled(ILogger logger, string modelId, string revision, string modelDigestPrefix);

    [LoggerMessage(
        2321,
        LogLevel.Warning,
        "Local relevance judge is not available ({ErrorCode}): {Detail} Relevance calls are refused until an install succeeds.")]
    private static partial void LogInstallFailed(ILogger logger, string errorCode, string detail);

    [LoggerMessage(
        2322,
        LogLevel.Information,
        "No local relevance judge is configured on this host: {Setting} is not set, so none is installed" +
        " and every relevance call is refused with relevance_judge_not_configured.")]
    private static partial void LogNotConfigured(ILogger logger, string setting);
}
