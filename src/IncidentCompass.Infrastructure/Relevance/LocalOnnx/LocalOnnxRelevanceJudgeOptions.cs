using IncidentCompass.Infrastructure.EmbeddingModels;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// Host settings of the in-process relevance judge: the directory its artifacts live in, the pinned
/// artifacts an empty directory is filled from, and how the model is run. Bound from
/// <c>IncidentCompass:RelevanceJudge:LocalOnnx</c>.
/// <para>
/// The defaults are the <c>BAAI/bge-reranker-v2-m3</c> cross-encoder: one int8 ONNX file and the
/// XLM-RoBERTa SentencePiece tokenizer, each with its SHA-256. They say what an empty model
/// directory is filled with. Once a manifest is installed in the directory, the installed manifest
/// is what runs, and a changed default never replaces it.
/// </para>
/// <para>
/// The judge needs a model directory of its own. A directory holds one active manifest, so the
/// embedding model and the judge cannot share one.
/// </para>
/// </summary>
internal sealed class LocalOnnxRelevanceJudgeOptions
{
    public const string SectionName = "IncidentCompass:RelevanceJudge:LocalOnnx";

    public const string DefaultModelId = "BAAI/bge-reranker-v2-m3";

    /// <summary>
    /// The pinned revision of the weights repository, which is where the tokenizer comes from. The
    /// ONNX file comes from a third-party export at a revision of its own, so the two URLs below name
    /// two different repositories at two different revisions. Both survive, because the installed
    /// manifest records each artifact's own URL.
    /// </summary>
    public const string DefaultRevision = "953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e";

    /// <summary>
    /// The same 1 GiB default the embedding options use. The pinned ONNX file is 570 727 094 bytes,
    /// so the default admits it with room to spare; a smaller configured cap would refuse the
    /// download before the digest is ever reached.
    /// </summary>
    public const long DefaultMaxDownloadBytes = 1L << 30;

    private const string WeightsResolveBaseUrl =
        "https://huggingface.co/" + DefaultModelId + "/resolve/" + DefaultRevision + "/";

    private const string OnnxExportResolveBaseUrl =
        "https://huggingface.co/onnx-community/bge-reranker-v2-m3-ONNX/resolve/" +
        "6f5ff65298512715a1e669753bc754d2bc8f367b/";

    /// <summary>
    /// The absolute directory that holds the active manifest and the artifact files. Required; there
    /// is no default, because the directory is a deployment decision (a volume), not a property of
    /// the model.
    /// </summary>
    public string? ModelDirectory { get; init; }

    public string ModelId { get; init; } = DefaultModelId;

    public string Revision { get; init; } = DefaultRevision;

    public string ModelFileUrl { get; init; } = OnnxExportResolveBaseUrl + "onnx/model_int8.onnx";

    public string ModelFileSha256 { get; init; } =
        "912fc1215c2dbff6499700534bd8d31253af01573861abbfc43afd1fab6cce5d";

    public string TokenizerFileUrl { get; init; } = WeightsResolveBaseUrl + "sentencepiece.bpe.model";

    /// <summary>
    /// The same digest the embedding options pin, and not a copy-and-paste mistake: this judge and
    /// the pinned embedding model are both XLM-RoBERTa models and their SentencePiece file is the
    /// same file, byte for byte. Each model directory still installs its own copy, because a
    /// directory holds one model's artifacts.
    /// </summary>
    public string TokenizerFileSha256 { get; init; } =
        "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865";

    /// <summary>
    /// The model's token window, both segments and all four sequence markers included. A pair beyond
    /// it is truncated, because the graph fails outright on a longer sequence rather than ignoring
    /// the excess.
    /// </summary>
    public int MaxTokens { get; init; } = 512;

    public string License { get; init; } = "Apache-2.0";

    /// <summary>
    /// ONNX Runtime intra-op threads for one scored pair: 1 by default, at most 16, for the same
    /// reason the embedding adapter fixes it at one. Inter-op parallelism is fixed at one thread and
    /// execution is sequential: the graph is one linear encoder with no parallel branches to
    /// schedule, and calls are already serialized by the adapter.
    /// </summary>
    public int IntraOpThreads { get; init; } = 1;

    /// <summary>
    /// The bound on one install pass, fetch and verification included. It is longer than the
    /// embedding model's because the pinned file is about five times the size.
    /// </summary>
    public int InstallTimeoutSeconds { get; init; } = 1800;

    /// <summary>
    /// The most bytes one artifact download may deliver. A response that declares a larger length is
    /// refused before anything is written, and a body that runs past the limit without declaring it
    /// is cut off and discarded.
    /// </summary>
    public long MaxDownloadBytes { get; init; } = DefaultMaxDownloadBytes;

    /// <summary>
    /// These settings as the pinned artifact set the model store installs. The store knows nothing
    /// about relevance judging; this is where a judge host says that its two files are an ONNX model
    /// and a SentencePiece tokenizer. It carries no embedding profile, because a cross-encoder has
    /// no vector width, no pooling and no prefixes.
    /// </summary>
    public LocalOnnxModelPin CreatePin() => new(
        LocalOnnxModelManifest.RelevanceJudgeKind,
        ModelId,
        Revision,
        License,
        ModelDirectory,
        MaxDownloadBytes,
        InstallTimeoutSeconds,
        MaxTokens,
        new LocalOnnxPinnedArtifact(ModelFileUrl, ModelFileSha256, LocalOnnxModelArtifact.OnnxKind),
        new LocalOnnxPinnedArtifact(TokenizerFileUrl, TokenizerFileSha256, LocalOnnxModelArtifact.SentencePieceKind),
        EmbeddingProfile: null);
}
