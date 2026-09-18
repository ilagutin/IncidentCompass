using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// Turns a model-store error code into the judge's own.
/// <para>
/// The store is shared and its vocabulary is spelled for the model it was written for: every constant
/// in <see cref="LocalOnnxModelErrorCodes" /> begins <c>embedding_model_</c>. Those strings are
/// correct where the store is installing an embedding model and wrong everywhere else, so they are
/// translated here rather than renamed there: renaming would change the codes an embedding refusal
/// has always carried, which is a public behavior change for a cosmetic reason.
/// </para>
/// <para>
/// This runs at the two points a store code enters the judge's world, the install pass and the
/// installed-judge reader, so everything downstream of them, including the recorded install state,
/// carries judge codes only. The normalized codes an application-level caller sees are unaffected:
/// this is the provider code beneath <c>memory_relevance_judge_unavailable</c>.
/// </para>
/// </summary>
internal static class LocalOnnxRelevanceJudgeStoreErrorCodeMap
{
    public static string Map(string? storeErrorCode) => storeErrorCode switch
    {
        LocalOnnxModelErrorCodes.NotInstalled => LocalOnnxRelevanceJudgeProvider.ModelNotInstalledErrorCode,
        LocalOnnxModelErrorCodes.DigestMismatch => LocalOnnxRelevanceJudgeProvider.ModelDigestMismatchErrorCode,
        LocalOnnxModelErrorCodes.FileMissing => LocalOnnxRelevanceJudgeProvider.ModelFileMissingErrorCode,
        LocalOnnxModelErrorCodes.FetchFailed => LocalOnnxRelevanceJudgeProvider.ModelFetchFailedErrorCode,
        LocalOnnxModelErrorCodes.DownloadTooLarge => LocalOnnxRelevanceJudgeProvider.ModelDownloadTooLargeErrorCode,
        LocalOnnxModelErrorCodes.InstallTimedOut => LocalOnnxRelevanceJudgeProvider.InstallTimedOutErrorCode,
        LocalOnnxModelErrorCodes.ManifestInvalid => LocalOnnxRelevanceJudgeProvider.ManifestInvalidErrorCode,
        LocalOnnxModelErrorCodes.StoreUnavailable => LocalOnnxRelevanceJudgeProvider.StoreUnavailableErrorCode,

        // A store code with no judge-side name. It is reported as unusable rather than as whichever
        // of the codes above looks closest, because guessing here is how a judge ends up describing
        // a failure it did not have. A test over the store's declared constants keeps this arm
        // unreachable in practice.
        _ => LocalOnnxRelevanceJudgeProvider.ModelUnusableErrorCode
    };
}
