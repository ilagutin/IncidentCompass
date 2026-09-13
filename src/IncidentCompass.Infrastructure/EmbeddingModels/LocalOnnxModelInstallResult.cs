namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <param name="Model">The configured model, verified and now active.</param>
/// <param name="ManifestSwitched">
/// False when the configured model was already the active one and the call only verified it.
/// </param>
/// <param name="PreviousManifestPath">
/// Where the manifest that was active before the switch is kept, or <see langword="null" /> when no
/// manifest was replaced. Restoring that file over the active manifest is the rollback.
/// </param>
internal sealed record LocalOnnxModelInstallResult(
    LocalOnnxInstalledModel Model,
    bool ManifestSwitched,
    string? PreviousManifestPath);
