namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// What is installed in a model directory: the model's identity, both artifact files with the URL
/// each came from and its SHA-256, and every setting the adapter needs to run it. The manifest is
/// the unit of installation. The adapter runs what the installed manifest says, not what the host
/// options say, so changing a default never changes an installed host.
/// <para>
/// <see cref="Kind" /> says what the artifacts are for, because a model directory now holds more
/// than an embedding model. The embedding-only settings are required of an embedding manifest and
/// absent from every other kind, which is why they are nullable here and checked against the kind
/// when a manifest is read. A schema 1 manifest, written by a shipped release before there were
/// other kinds, carries no kind field and is read as the embedding manifest it always was.
/// </para>
/// </summary>
internal sealed record LocalOnnxModelManifest(
    int SchemaVersion,
    string Id,
    string Revision,
    LocalOnnxModelArtifact ModelFile,
    LocalOnnxModelArtifact TokenizerFile,
    int MaxTokens,
    string License,
    string? Kind = null,
    int? Dimensions = null,
    string? Pooling = null,
    bool? Normalize = null,
    string? QueryPrefix = null,
    string? PassagePrefix = null)
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// The schema a shipped release wrote: embedding-only, with no kind field. It is still read, so
    /// upgrading a host does not orphan the manifest already on its model volume.
    /// </summary>
    public const int LegacyEmbeddingSchemaVersion = 1;

    /// <summary>An embedding model: the only kind schema 1 could describe.</summary>
    public const string EmbeddingKind = "embedding";

    /// <summary>A cross-encoder that scores how relevant a passage is to a query.</summary>
    public const string RelevanceJudgeKind = "relevance_judge";

    /// <summary>
    /// The embedding-only settings of an embedding manifest. Asking any other kind for them is a
    /// programming error rather than an operator's mistake: the serializer refuses an embedding
    /// manifest that lacks one of them before it ever reaches a caller.
    /// </summary>
    public LocalOnnxEmbeddingProfile GetEmbeddingProfile() =>
        string.Equals(Kind, EmbeddingKind, StringComparison.Ordinal) &&
        Dimensions is { } dimensions &&
        Pooling is { } pooling &&
        Normalize is { } normalize &&
        QueryPrefix is { } queryPrefix &&
        PassagePrefix is { } passagePrefix
            ? new LocalOnnxEmbeddingProfile(dimensions, pooling, normalize, queryPrefix, passagePrefix)
            : throw new InvalidOperationException(
                $"The installed manifest of {Id} is of kind '{Kind}' and carries no embedding settings.");
}
