using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.ML.Tokenizers;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// Turns one (query, passage) pair into the cross-encoder's input ids: trim both, tokenize each with
/// the SentencePiece model, map to the model's vocabulary and join them into one sequence.
/// <para>
/// The layout is the one <c>XLMRobertaTokenizerFast</c> builds for a pair:
/// <c>&lt;s&gt; query &lt;/s&gt; &lt;/s&gt; passage &lt;/s&gt;</c>, four markers in total. There is
/// no prefix on either segment: a cross-encoder reads the raw text, unlike the embedding model,
/// whose manifest carries a query and a passage prefix. The model emits no token type ids for this
/// layout and its attention mask is all ones, so neither is built here.
/// </para>
/// <para>
/// Content is capped at <see cref="LocalOnnxModelManifest.MaxTokens" /> minus the four markers,
/// because the model fails outright on a longer sequence. The query is allowed at most half that
/// budget and the passage takes the rest, so a short query still leaves the passage almost the whole
/// window while a long one can never take it all. Both cuts land on a token boundary, never inside a
/// token, because the cap is applied while tokenizing rather than to the text. This differs from the
/// reference tokenizer's default, which shortens whichever segment is currently longer; the two
/// agree for every pair that fits.
/// </para>
/// <para>
/// The half is not a style choice. Giving the query the whole budget first let a long enough query
/// leave the passage nothing, and every candidate then scored against an empty passage receives the
/// same score, so the judge admits all of them or none whatever they contain. Splitting the budget
/// keeps every candidate's own text in the sequence. The caller bounds the query as well, so at the
/// pinned window this cut is a backstop rather than the normal path.
/// </para>
/// </summary>
internal sealed class LocalOnnxPairEncoder
{
    /// <summary>The one opening and three separating or closing markers a pair carries.</summary>
    public const int SpecialTokenCount = 4;

    private readonly SentencePieceTokenizer tokenizer;
    private readonly int maxContentTokens;

    private LocalOnnxPairEncoder(SentencePieceTokenizer tokenizer, LocalOnnxModelManifest manifest)
    {
        this.tokenizer = tokenizer;
        maxContentTokens = manifest.MaxTokens - SpecialTokenCount;
        ArgumentOutOfRangeException.ThrowIfLessThan(maxContentTokens, 1, nameof(manifest));
    }

    internal SentencePieceTokenizer Tokenizer => tokenizer;

    internal int MaxContentTokens => maxContentTokens;

    /// <summary>
    /// The most tokens the query may occupy: half the content budget, and never less than one, so a
    /// window too narrow to split at all still asks a question. Whatever the query does not use goes
    /// to the passage, so the passage is never given less than the other half.
    /// </summary>
    internal int MaxQueryTokens => Math.Max(1, maxContentTokens / 2);

    public static LocalOnnxPairEncoder Load(string tokenizerFilePath, LocalOnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var stream = new FileStream(tokenizerFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new LocalOnnxPairEncoder(
            SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false),
            manifest);
    }

    public long[] Encode(string query, string passage)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(passage);
        var queryIds = EncodeSegment(query, MaxQueryTokens);
        var passageIds = EncodeSegment(passage, maxContentTokens - queryIds.Count);

        var ids = new long[queryIds.Count + passageIds.Count + SpecialTokenCount];
        var next = 0;
        ids[next++] = LocalOnnxSentencePieceVocabulary.BeginningOfSequenceId;
        next = Append(ids, next, queryIds);
        ids[next++] = LocalOnnxSentencePieceVocabulary.EndOfSequenceId;
        ids[next++] = LocalOnnxSentencePieceVocabulary.EndOfSequenceId;
        next = Append(ids, next, passageIds);
        ids[next] = LocalOnnxSentencePieceVocabulary.EndOfSequenceId;
        return ids;
    }

    private IReadOnlyList<int> EncodeSegment(string text, int budget) =>
        budget <= 0
            ? []
            : tokenizer.EncodeToIds(
                text.Trim(),
                addBeginningOfSentence: false,
                addEndOfSentence: false,
                maxTokenCount: budget,
                out _,
                out _);

    private static int Append(long[] ids, int next, IReadOnlyList<int> sentencePieceIds)
    {
        for (var index = 0; index < sentencePieceIds.Count; index++)
        {
            ids[next + index] = LocalOnnxSentencePieceVocabulary.MapSentencePieceId(sentencePieceIds[index]);
        }

        return next + sentencePieceIds.Count;
    }
}
