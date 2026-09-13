namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// The outcome of the latest install pass, shared by the install hosted service that writes it and
/// the adapter that reads it on every call. Each change replaces one immutable snapshot, so a reader
/// always sees a consistent status, model and code together.
/// </summary>
internal sealed class LocalOnnxModelInstallState
{
    private volatile LocalOnnxModelInstallSnapshot snapshot = LocalOnnxModelInstallSnapshot.NotStarted;

    public LocalOnnxModelInstallSnapshot Snapshot => snapshot;

    public void RecordInstalling() =>
        snapshot = new LocalOnnxModelInstallSnapshot(LocalOnnxModelInstallStatus.Installing, null, null, null);

    public void RecordInstalled(LocalOnnxInstalledModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        snapshot = new LocalOnnxModelInstallSnapshot(LocalOnnxModelInstallStatus.Installed, model, null, null);
    }

    public void RecordFailed(string errorCode, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        snapshot = new LocalOnnxModelInstallSnapshot(LocalOnnxModelInstallStatus.Failed, null, errorCode, detail);
    }
}
