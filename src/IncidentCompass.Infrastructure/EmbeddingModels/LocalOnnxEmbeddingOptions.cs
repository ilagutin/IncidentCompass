namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// Host settings of the in-process embedding model: the directory its artifacts live in, the pinned
/// artifacts an empty directory is filled from, and how the model is run. Bound from
/// <c>IncidentCompass:Embeddings:LocalOnnx</c> and validated at start only when
/// <c>IncidentCompass:Embeddings:Provider</c> is <c>LocalOnnx</c>.
/// <para>
/// The defaults are the <c>intfloat/multilingual-e5-small</c> artifacts from one pinned Hugging Face
/// revision: the int8 ONNX file and the SentencePiece tokenizer, each with its SHA-256. They say what
/// an empty model directory is filled with. Once a manifest is installed in the directory, the
/// installed manifest is what runs, and a changed default never replaces it.
/// </para>
/// </summary>
internal sealed class LocalOnnxEmbeddingOptions
{
    public const string SectionName = "IncidentCompass:Embeddings:LocalOnnx";

    public const string MeanPooling = "mean";

    public const string DefaultModelId = "intfloat/multilingual-e5-small";

    public const string DefaultRevision = "614241f622f53c4eeff9890bdc4f31cfecc418b3";

    public const long DefaultMaxDownloadBytes = 1L << 30;

    private const string DefaultResolveBaseUrl =
        "https://huggingface.co/" + DefaultModelId + "/resolve/" + DefaultRevision + "/";

    /// <summary>
    /// The absolute directory that holds the active manifest and the artifact files. Required when
    /// the embedding provider is <c>LocalOnnx</c>; there is no default, because the directory is a
    /// deployment decision (a volume), not a property of the model.
    /// </summary>
    public string? ModelDirectory { get; init; }

    public string ModelId { get; init; } = DefaultModelId;

    public string Revision { get; init; } = DefaultRevision;

    public string ModelFileUrl { get; init; } = DefaultResolveBaseUrl + "onnx/model_qint8_avx512_vnni.onnx";

    public string ModelFileSha256 { get; init; } = "dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88";

    public string TokenizerFileUrl { get; init; } = DefaultResolveBaseUrl + "onnx/sentencepiece.bpe.model";

    public string TokenizerFileSha256 { get; init; } = "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865";

    public int Dimensions { get; init; } = 384;

    /// <summary>
    /// The model's token window, including the two sequence markers. Input beyond it is truncated,
    /// because the graph fails outright on a longer sequence rather than ignoring the excess.
    /// </summary>
    public int MaxTokens { get; init; } = 512;

    public string QueryPrefix { get; init; } = "query: ";

    public string PassagePrefix { get; init; } = "passage: ";

    public string Pooling { get; init; } = MeanPooling;

    public bool Normalize { get; init; } = true;

    public string License { get; init; } = "MIT";

    /// <summary>
    /// ONNX Runtime intra-op threads for one embedding call: 1 by default, at most 16. The Worker shares
    /// one host with PostgreSQL, the Api and its own claim loop, and one thread keeps an embedding
    /// call to one core whatever the machine has, which is what single-host sizing can rely on. A
    /// default derived from the processor count was rejected because the runtime sees the host's
    /// cores, not a container's CPU quota. Inter-op parallelism is fixed at one thread and execution
    /// is sequential: the graph is one linear encoder with no parallel branches to schedule, and calls
    /// are already serialized by the adapter. Measured on the pinned model with one thread: about 4 ms
    /// for a 14-token query and about 180 ms for a full 512-token passage on a desktop x64 CPU.
    /// </summary>
    public int IntraOpThreads { get; init; } = 1;

    /// <summary>
    /// The bound on one install pass at start, fetch and verification included. A pass that runs past
    /// it is recorded as a named failure and the host keeps starting. With memory seeding enabled, the
    /// seed pass then publishes nothing and records <c>memory_embedding_model_unavailable</c>; the
    /// previous corpus stays current.
    /// </summary>
    public int InstallTimeoutSeconds { get; init; } = 900;

    /// <summary>
    /// The most bytes one artifact download may deliver: 1 GiB by default, between 1 byte and 16 GiB.
    /// A response that declares a larger length is refused before anything is written, and a body
    /// that runs past the limit without declaring it is cut off and discarded, with
    /// <c>embedding_model_download_too_large</c>. The digest already rejects a wrong file; this bound
    /// keeps a misbehaving origin from filling the model volume, which a single host shares with
    /// PostgreSQL, before the digest check is reached.
    /// </summary>
    public long MaxDownloadBytes { get; init; } = DefaultMaxDownloadBytes;
}
