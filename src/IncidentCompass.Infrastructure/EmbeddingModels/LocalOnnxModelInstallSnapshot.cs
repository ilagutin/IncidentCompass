namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <param name="Status">Where the install pass is.</param>
/// <param name="Model">The verified model, present only when <paramref name="Status" /> is installed.</param>
/// <param name="ErrorCode">A code from <see cref="LocalOnnxModelErrorCodes" /> when the pass failed.</param>
/// <param name="Detail">An operator-facing sentence about the failure: paths, digests, hosts, never file content.</param>
internal sealed record LocalOnnxModelInstallSnapshot(
    LocalOnnxModelInstallStatus Status,
    LocalOnnxInstalledModel? Model,
    string? ErrorCode,
    string? Detail)
{
    public static LocalOnnxModelInstallSnapshot NotStarted { get; } =
        new(LocalOnnxModelInstallStatus.NotStarted, null, null, null);
}
