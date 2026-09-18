using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

internal static class LocalOnnxRelevanceJudgeProvider
{
    /// <summary>
    /// The adapter family's name, the same one the local embedding adapter reports: both are the
    /// in-process ONNX runtime on this host, and a failure names which call kind it came from through
    /// its error code rather than through a second provider name.
    /// </summary>
    public const string Name = "local-onnx";

    /// <summary>
    /// The stable error code of a call that reached the adapter on a host where no local judge is
    /// installed. It is the model store's own code, because the store is what answers that question.
    /// </summary>
    public const string ModelNotInstalledErrorCode = LocalOnnxModelErrorCodes.NotInstalled;

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
