namespace IncidentCompass.Application.Core.Embeddings;

/// <summary>
/// What an embedding input is for: a search query compared against the corpus, or a passage stored
/// in it. Some embedding models encode the two differently, and a query embedded as a passage is
/// silently worse rather than failing, so every request states which one it is.
/// </summary>
/// <remarks>
/// The values start at one so that an unset <see langword="default" /> is not a valid kind.
/// </remarks>
public enum EmbeddingInputKind
{
    Query = 1,
    Passage = 2
}
