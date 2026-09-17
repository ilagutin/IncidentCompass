using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.ML.Tokenizers;

namespace IncidentCompass.Infrastructure.Embeddings.LocalOnnx;

/// <summary>
/// Turns one request input into the model's input ids: trim, prepend the manifest's prefix for the
/// input kind, tokenize with the SentencePiece model, cap, and map to the model's vocabulary.
/// <para>
/// The tokenizer returns raw SentencePiece ids, where <c>&lt;unk&gt;</c>=0, <c>&lt;s&gt;</c>=1 and
/// <c>&lt;/s&gt;</c>=2. XLM-R style models use the fairseq vocabulary, which puts
/// <c>&lt;s&gt;</c>=0, <c>&lt;pad&gt;</c>=1, <c>&lt;/s&gt;</c>=2, <c>&lt;unk&gt;</c>=3 first and
/// shifts every other piece up by one. The sequence is then wrapped in <c>&lt;s&gt;</c> ...
/// <c>&lt;/s&gt;</c>. This matches the reference Hugging Face tokenizer id for id on English, Polish
/// and Russian input, with one known difference: that tokenizer keeps a lone trailing whitespace
/// piece which SentencePiece drops, and trimming the input first removes the case.
/// </para>
/// <para>
/// Content is capped at <see cref="LocalOnnxModelManifest.MaxTokens" /> minus the two markers,
/// because the model fails outright on a longer sequence. The prefix counts toward the cap.
/// </para>
/// </summary>
internal sealed class LocalOnnxInputEncoder
{
    public const long BeginningOfSequenceId = 0;
    public const long EndOfSequenceId = 2;

    private readonly SentencePieceTokenizer tokenizer;
    private readonly string queryPrefix;
    private readonly string passagePrefix;
    private readonly int maxContentTokens;

    internal SentencePieceTokenizer Tokenizer => tokenizer;

    internal string PassagePrefix => passagePrefix;

    private LocalOnnxInputEncoder(SentencePieceTokenizer tokenizer, LocalOnnxModelManifest manifest)
    {
        this.tokenizer = tokenizer;
        var profile = manifest.GetEmbeddingProfile();
        queryPrefix = profile.QueryPrefix;
        passagePrefix = profile.PassagePrefix;
        maxContentTokens = manifest.MaxTokens - 2;
    }

    public static LocalOnnxInputEncoder Load(string tokenizerFilePath, LocalOnnxModelManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var stream = new FileStream(tokenizerFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new LocalOnnxInputEncoder(
            SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false),
            manifest);
    }

    public long[] Encode(string input, EmbeddingInputKind kind)
    {
        ArgumentNullException.ThrowIfNull(input);
        var prefix = kind switch
        {
            EmbeddingInputKind.Query => queryPrefix,
            EmbeddingInputKind.Passage => passagePrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The input kind is not a defined kind.")
        };

        var contentIds = tokenizer.EncodeToIds(
            prefix + input.Trim(),
            addBeginningOfSentence: false,
            addEndOfSentence: false,
            maxTokenCount: maxContentTokens,
            out _,
            out _);

        var ids = new long[contentIds.Count + 2];
        ids[0] = BeginningOfSequenceId;
        for (var index = 0; index < contentIds.Count; index++)
        {
            ids[index + 1] = MapSentencePieceId(contentIds[index]);
        }

        ids[^1] = EndOfSequenceId;
        return ids;
    }

    public static long MapSentencePieceId(int sentencePieceId) => sentencePieceId switch
    {
        0 => 3,
        1 => 0,
        2 => 2,
        _ => sentencePieceId + 1L
    };
}
