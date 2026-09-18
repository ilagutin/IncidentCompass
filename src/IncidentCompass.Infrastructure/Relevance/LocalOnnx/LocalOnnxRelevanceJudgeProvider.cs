using IncidentCompass.Application.Memory;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// The local judge adapter's name and its stable error codes.
/// </summary>
/// <remarks>
/// Every code here is the judge's own. The model store the judge installs through has an error
/// vocabulary of its own, and every constant in it is spelled <c>embedding_model_...</c> because the
/// store was written for the embedding model; those strings must never reach a judge surface, where
/// they would name the wrong model. <see cref="LocalOnnxRelevanceJudgeStoreErrorCodeMap" /> is where
/// a store code becomes one of these, and it is applied at the two places a store code enters the
/// judge's world: the install pass and the installed-judge reader.
/// </remarks>
internal static class LocalOnnxRelevanceJudgeProvider
{
    /// <summary>
    /// The adapter family's name, the same one the local embedding adapter reports: both are the
    /// in-process ONNX runtime on this host, and a failure names which call kind it came from through
    /// its error code rather than through a second provider name.
    /// </summary>
    public const string Name = "local-onnx";

    /// <summary>
    /// This host runs no judge at all: no model directory is configured for one. It is a different
    /// answer from every code below, all of which describe a judge this host meant to run, and it is
    /// deliberately not reported as an unavailable store: nothing is wrong with the store.
    /// <para>
    /// This code and <see cref="ModelNotInstalledErrorCode" /> are the only two an Application caller
    /// may answer without a judge, so their spelling is owned by
    /// <see cref="MemoryRelevanceJudgeAbsence" /> and taken from there. Every other code in this file
    /// is the adapter's own and propagates.
    /// </para>
    /// </summary>
    public const string NotConfiguredErrorCode = MemoryRelevanceJudgeAbsence.NotConfigured;

    /// <summary>The stable error code of a call on a host where no install pass has put a judge in place.</summary>
    public const string ModelNotInstalledErrorCode = MemoryRelevanceJudgeAbsence.NotInstalled;

    /// <summary>
    /// The judge's install pass is running and has not finished. Deliberately not
    /// <see cref="ModelNotInstalledErrorCode" />: that code tells the Application this host runs no
    /// judge, and the Application then admits on the lexical gate alone. A host part way through
    /// fetching a judge is a host that meant to have one, and the pinned judge is over half a
    /// gigabyte with an install timeout measured in the hundreds of seconds, so reporting the window
    /// as judge-lessness would confirm <c>KnownIncident</c> on unjudged bands for as long as the
    /// download takes. Propagating instead refuses those calls with a configuration-required code and
    /// lets the job retry after the install finishes.
    /// </summary>
    public const string InstallInProgressErrorCode = "relevance_judge_install_in_progress";

    /// <summary>A judge file's SHA-256 is not the pinned one. The file is never repaired or replaced.</summary>
    public const string ModelDigestMismatchErrorCode = "relevance_judge_model_digest_mismatch";

    /// <summary>The installed judge manifest names a file that is not on disk.</summary>
    public const string ModelFileMissingErrorCode = "relevance_judge_model_file_missing";

    /// <summary>A missing judge file could not be downloaded; nothing was renamed into place.</summary>
    public const string ModelFetchFailedErrorCode = "relevance_judge_model_fetch_failed";

    /// <summary>A judge download declared or delivered more bytes than the configured limit.</summary>
    public const string ModelDownloadTooLargeErrorCode = "relevance_judge_model_download_too_large";

    /// <summary>The judge install pass ran past its configured bound.</summary>
    public const string InstallTimedOutErrorCode = "relevance_judge_install_timed_out";

    /// <summary>The installed judge manifest is unreadable, incomplete or names a path outside the directory.</summary>
    public const string ManifestInvalidErrorCode = "relevance_judge_manifest_invalid";

    /// <summary>The judge's model directory could not be read or written.</summary>
    public const string StoreUnavailableErrorCode = "relevance_judge_store_unavailable";

    /// <summary>
    /// The judge is not usable and the store's reason has no judge-side name yet. It exists so that a
    /// code added to the store cannot leak through the map untranslated; a test asserts that every
    /// store code the solution declares maps to one of the codes above instead, so reaching this one
    /// means the store grew a code and nobody said what it means for a judge.
    /// </summary>
    public const string ModelUnusableErrorCode = "relevance_judge_model_unusable";

    /// <summary>
    /// The installed judge is not the configured one, or is not a judge at all. Carried as the
    /// provider error code; the refusal's normalized code is <c>memory_relevance_judge_mismatch</c>.
    /// </summary>
    public const string ModelMismatchErrorCode = "relevance_judge_model_mismatch";

    /// <summary>The verified model or tokenizer file could not be loaded into the runtime.</summary>
    public const string ModelLoadFailedErrorCode = "relevance_judge_model_load_failed";

    /// <summary>The runtime failed while running the model.</summary>
    public const string InferenceFailedErrorCode = "relevance_judge_inference_failed";

    /// <summary>
    /// The graph produced something other than one score per row, so there is no single float to
    /// read. Refused by name rather than by taking element zero of whatever came back.
    /// </summary>
    public const string OutputShapeInvalidErrorCode = "relevance_judge_output_shape_invalid";
}
