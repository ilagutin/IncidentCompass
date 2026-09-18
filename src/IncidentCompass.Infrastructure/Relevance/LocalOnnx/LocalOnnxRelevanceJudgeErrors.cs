using IncidentCompass.Application.Core.Errors;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// The local judge adapter's failures as the port's exception, following the vocabulary
/// <c>LocalOnnxEmbeddingErrors</c> established. No message carries the query, a candidate or a score;
/// they name codes, models, shapes and settings.
/// </summary>
internal static class LocalOnnxRelevanceJudgeErrors
{
    /// <summary>
    /// A configuration state rather than an outage or a rejection: the call is well formed, and what
    /// is missing is the judge this host is configured for. It keeps being refused until an operator
    /// installs the configured judge or corrects the configuration and restarts the host, because a
    /// running host keeps the model it verified. The normalized code is
    /// <c>memory_relevance_judge_mismatch</c>; the adapter's own code is kept as the provider code.
    /// </summary>
    public static MemoryRelevanceJudgeException ModelMismatch(string configuredModel, string installedModel) =>
        CreateConfigurationRequired(
            $"The host is configured for relevance judge '{configuredModel}', but the installed local model is" +
            $" '{installedModel}'.",
            MemoryRelevanceJudgeErrorCodes.Mismatch,
            LocalOnnxRelevanceJudgeProvider.ModelMismatchErrorCode);

    /// <summary>
    /// The installed manifest is of another kind, which is the same configuration state as a
    /// mismatch: the directory holds a model, and it is not a judge.
    /// </summary>
    public static MemoryRelevanceJudgeException ModelKindMismatch(string installedModel, string? installedKind) =>
        CreateConfigurationRequired(
            $"The installed local model '{installedModel}' is of kind '{installedKind}', not" +
            $" '{LocalOnnxModelManifest.RelevanceJudgeKind}', so it cannot score relevance.",
            MemoryRelevanceJudgeErrorCodes.Mismatch,
            LocalOnnxRelevanceJudgeProvider.ModelMismatchErrorCode);

    /// <summary>
    /// No usable installed judge, which is the same configuration state as a mismatch, reported as
    /// <c>memory_relevance_judge_unavailable</c>. The judge code that says why, for example
    /// <c>relevance_judge_model_digest_mismatch</c>, is kept as the provider error code; the reader
    /// has already translated the model store's own vocabulary into the judge's.
    /// </summary>
    public static MemoryRelevanceJudgeException NotAvailable(LocalOnnxInstalledModelLookup lookup) =>
        CreateConfigurationRequired(
            lookup.Detail ?? "The local relevance judge is not installed on this host.",
            MemoryRelevanceJudgeErrorCodes.Unavailable,
            lookup.ErrorCode ?? LocalOnnxRelevanceJudgeProvider.ModelNotInstalledErrorCode);

    public static MemoryRelevanceJudgeException LoadFailed(string detail, Exception? innerException = null) =>
        Create(
            "The installed local relevance judge could not be loaded: " + detail,
            LocalOnnxRelevanceJudgeProvider.ModelLoadFailedErrorCode,
            ProviderFailureKind.Unavailable,
            innerException);

    public static MemoryRelevanceJudgeException InferenceFailed(Exception innerException) =>
        Create(
            $"The local relevance judge failed while running ({innerException.GetType().Name}).",
            LocalOnnxRelevanceJudgeProvider.InferenceFailedErrorCode,
            ProviderFailureKind.InvalidResponse,
            innerException);

    public static MemoryRelevanceJudgeException OutputShapeInvalid(IReadOnlyList<long> shape) =>
        Create(
            $"The local relevance judge produced a logits tensor of shape [{string.Join(", ", shape)}], but one" +
            " scored pair must produce exactly one score.",
            LocalOnnxRelevanceJudgeProvider.OutputShapeInvalidErrorCode,
            ProviderFailureKind.InvalidResponse);

    private static MemoryRelevanceJudgeException Create(
        string message,
        string errorCode,
        ProviderFailureKind failureKind,
        Exception? innerException = null) =>
        new(
            LocalOnnxRelevanceJudgeProvider.Name,
            message,
            errorCode: errorCode,
            innerException: innerException,
            failureKind: failureKind);

    private static MemoryRelevanceJudgeException CreateConfigurationRequired(
        string message,
        string errorCode,
        string adapterErrorCode) =>
        new(
            LocalOnnxRelevanceJudgeProvider.Name,
            message,
            errorCode: errorCode,
            providerErrorCode: adapterErrorCode,
            failureKind: ProviderFailureKind.ConfigurationRequired);
}
