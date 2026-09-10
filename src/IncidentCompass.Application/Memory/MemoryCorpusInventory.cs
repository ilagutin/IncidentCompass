namespace IncidentCompass.Application.Memory;

/// <summary>
/// What one owner's memory corpus actually contains right now: the generation the database calls
/// current, the distinct vector spaces its active chunks sit in, and how many rows there are.
/// </summary>
/// <remarks>
/// <see cref="ActiveIdentities" /> is read from the chunks themselves rather than trusted from
/// <see cref="Current" />. The recorded generation says what the last publish intended; the chunk
/// rows say what is there. More than one entry means a corpus that predates transactional
/// generation publication ended up holding two vector spaces at once, which is worth reporting
/// even though the publish path can no longer produce it.
/// </remarks>
internal sealed record MemoryCorpusInventory(
    MemoryCorpusGeneration? Current,
    IReadOnlyList<MemoryCorpusIdentity> ActiveIdentities,
    int ActiveItemCount,
    int ActiveChunkCount);
