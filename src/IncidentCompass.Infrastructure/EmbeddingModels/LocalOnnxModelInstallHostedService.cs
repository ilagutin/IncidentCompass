using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// Installs or verifies the local embedding model while the Worker starts, on a host whose embedding
/// provider is <c>LocalOnnx</c>. The work runs inside <see cref="StartAsync" /> on purpose: the
/// generic host starts hosted services one after another, and this one is registered before the
/// memory seed pass, so the first seed pass finds the model installed or finds the named failure.
/// <para>
/// A failed install does not fail this pass. The failure is recorded with its code in
/// <see cref="LocalOnnxModelInstallState" />, every embedding call is refused with that code, and
/// the host goes on to its next hosted service. What an unavailable model then means for the host
/// is decided by the memory seed pass: with memory seeding enabled it embeds at start, is refused
/// with the recorded code and fails the host start, exactly as an unreachable OpenAI-compatible
/// embedding endpoint does. The pass is bounded by
/// <see cref="LocalOnnxEmbeddingOptions.InstallTimeoutSeconds" /> and stops with the host.
/// </para>
/// </summary>
internal sealed partial class LocalOnnxModelInstallHostedService(
    IOptions<EmbeddingOptions> embeddingOptions,
    IOptions<LocalOnnxEmbeddingOptions> localOnnxOptions,
    LocalOnnxModelStore store,
    LocalOnnxModelInstallState installState,
    ILogger<LocalOnnxModelInstallHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!ProviderKindParser.IsLocalOnnx(embeddingOptions.Value.Provider))
        {
            return;
        }

        var options = localOnnxOptions.Value;
        installState.RecordInstalling();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.InstallTimeoutSeconds));
        using var installCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var installed = await store.EnsureInstalledAsync(options, installCancellation.Token);
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
                LocalOnnxModelErrorCodes.NotInstalled,
                "The host stopped before the local embedding model install completed.");
            throw;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            RecordFailure(
                LocalOnnxModelErrorCodes.InstallTimedOut,
                $"The local embedding model install did not complete within {options.InstallTimeoutSeconds} seconds.");
        }
        catch (LocalOnnxModelStoreException exception)
        {
            RecordFailure(exception.ErrorCode, exception.Message);
        }
        catch (Exception exception)
        {
            // Anything else is still a recorded install failure rather than an exception out of this
            // pass; the memory seed pass decides what an unavailable model means at start.
            RecordFailure(
                LocalOnnxModelErrorCodes.StoreUnavailable,
                $"The local embedding model install failed with {exception.GetType().Name}.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RecordFailure(string errorCode, string detail)
    {
        installState.RecordFailed(errorCode, detail);
        LogInstallFailed(logger, errorCode, detail);
    }

    [LoggerMessage(
        2310,
        LogLevel.Information,
        "Local embedding model {ModelId} at revision {Revision} is installed and verified (model file sha256 {ModelDigestPrefix}).")]
    private static partial void LogInstalled(ILogger logger, string modelId, string revision, string modelDigestPrefix);

    [LoggerMessage(
        2311,
        LogLevel.Warning,
        "Local embedding model is not available ({ErrorCode}): {Detail} Embedding calls are refused until an install succeeds.")]
    private static partial void LogInstallFailed(ILogger logger, string errorCode, string detail);
}
