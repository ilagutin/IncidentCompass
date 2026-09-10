namespace IncidentCompass.Application.Memory;

/// <summary>
/// The embedding route a memory corpus generation was built under, recorded so a later route
/// change is a detectable state rather than a corpus that silently stops matching.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RouteId" /> is carried for diagnostics and is deliberately not part of
/// <see cref="Matches" />: two routes naming the same provider and the same model produce the same
/// vectors, so retargeting <c>memory_search</c> between them does not invalidate a corpus.
/// </para>
/// <para>
/// <see cref="ProviderId" /> is compared, because the adapter name in
/// <see cref="EmbeddingProvider" /> cannot separate two configured providers: every
/// OpenAI-compatible provider reports one adapter name, so a configuration moved to a different
/// embedding server under the same model name is invisible without it.
/// </para>
/// <para>
/// Nothing here is a secret. A provider identifier is the configured key of an entry in the
/// provider table, never its endpoint and never its credential.
/// </para>
/// </remarks>
internal sealed record MemoryCorpusIdentity(
    string? RouteId,
    string? ProviderId,
    string EmbeddingProvider,
    string EmbeddingModel,
    int EmbeddingDimensions)
{
    /// <summary>
    /// Answers whether a corpus built under <paramref name="other" /> can still be retrieved under
    /// this identity.
    /// </summary>
    /// <remarks>
    /// A null <see cref="ProviderId" /> on either side means unrecorded, not different. Only a
    /// corpus published before provider identity was recorded can be in that state, and reporting
    /// a change that may not have happened would send an operator to rebuild a corpus that is
    /// probably fine. The embedding-response half of the check still protects that corpus: the
    /// first vector the next pass produces is compared against the recorded provider, model and
    /// dimensions before anything is published.
    /// </remarks>
    public bool Matches(MemoryCorpusIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(EmbeddingProvider, other.EmbeddingProvider, StringComparison.Ordinal) ||
            !string.Equals(EmbeddingModel, other.EmbeddingModel, StringComparison.Ordinal) ||
            EmbeddingDimensions != other.EmbeddingDimensions)
        {
            return false;
        }

        return ProviderId is null ||
            other.ProviderId is null ||
            string.Equals(ProviderId, other.ProviderId, StringComparison.Ordinal);
    }
}
