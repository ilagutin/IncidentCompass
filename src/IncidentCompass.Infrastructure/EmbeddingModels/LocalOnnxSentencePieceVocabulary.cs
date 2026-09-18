namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// How a SentencePiece id becomes a model input id for the XLM-RoBERTa family, and what the four
/// special ids are.
/// <para>
/// A SentencePiece model file puts <c>&lt;unk&gt;</c>=0, <c>&lt;s&gt;</c>=1 and <c>&lt;/s&gt;</c>=2
/// first. XLM-R style models use the fairseq vocabulary, which puts <c>&lt;s&gt;</c>=0,
/// <c>&lt;pad&gt;</c>=1, <c>&lt;/s&gt;</c>=2, <c>&lt;unk&gt;</c>=3 first and shifts every other piece
/// up by one.
/// </para>
/// <para>
/// This lives beside the model store rather than beside either encoder because it is a property of
/// the installed SentencePiece artifact, which the store owns, and because more than one kind of
/// model now runs on that artifact: the embedding encoder builds one sequence from it and the
/// relevance judge's pair encoder builds two. Writing the mapping a second time is how the two would
/// drift apart.
/// </para>
/// </summary>
internal static class LocalOnnxSentencePieceVocabulary
{
    /// <summary>The <c>&lt;s&gt;</c> marker that opens a sequence.</summary>
    public const long BeginningOfSequenceId = 0;

    /// <summary>The <c>&lt;pad&gt;</c> id. Nothing here pads; it is named so the layout is complete.</summary>
    public const long PaddingId = 1;

    /// <summary>The <c>&lt;/s&gt;</c> marker that closes a sequence and separates a pair's segments.</summary>
    public const long EndOfSequenceId = 2;

    /// <summary>The <c>&lt;unk&gt;</c> id a piece outside the vocabulary maps to.</summary>
    public const long UnknownId = 3;

    public static long MapSentencePieceId(int sentencePieceId) => sentencePieceId switch
    {
        0 => UnknownId,
        1 => BeginningOfSequenceId,
        2 => EndOfSequenceId,
        _ => sentencePieceId + 1L
    };
}
