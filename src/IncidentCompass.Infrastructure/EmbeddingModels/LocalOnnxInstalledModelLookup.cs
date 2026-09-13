namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <param name="Model">The verified installed model, or <see langword="null" /> when none is usable.</param>
/// <param name="ErrorCode">A code from <see cref="LocalOnnxModelErrorCodes" /> when no model is usable.</param>
/// <param name="Detail">An operator-facing sentence: paths, digests, hosts, never file content.</param>
internal sealed record LocalOnnxInstalledModelLookup(
    LocalOnnxInstalledModel? Model,
    string? ErrorCode,
    string? Detail)
{
    public static LocalOnnxInstalledModelLookup Found(LocalOnnxInstalledModel model) => new(model, null, null);

    public static LocalOnnxInstalledModelLookup NotAvailable(string errorCode, string detail) =>
        new(null, errorCode, detail);
}
