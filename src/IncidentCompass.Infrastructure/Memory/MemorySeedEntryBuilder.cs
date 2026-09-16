using System.Security.Cryptography;
using System.Text;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Memory;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// Turns one reviewed seed file into the item and chunks a reconciliation will publish, embedding
/// it only when the pass needs a new vector for it.
/// </summary>
/// <remarks>
/// An entry with no chunks means "this seed is already published exactly as it is on disk". Only
/// an incremental pass can produce one: a rebuild has to re-embed everything, because what a
/// rebuild is correcting is the vector space, and no comparison of file content can see that.
/// </remarks>
internal sealed class MemorySeedEntryBuilder(
    IEmbeddingClient embeddingClient,
    IMemoryRepository memoryRepository,
    MemoryDocumentChunker chunker)
{
    public async Task<MemorySeedEntry> BuildAsync(
        MemorySeedFile file,
        MemorySeedOptions settings,
        MemoryEmbeddingRoute route,
        bool forceEmbedding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(route);
        var item = CreateItem(file, settings, ComputeSha256Hex(file.Content));
        if (!forceEmbedding &&
            await memoryRepository.SeedItemExistsAsync(settings.Owner, item, cancellationToken))
        {
            return new MemorySeedEntry(item, []);
        }

        IReadOnlyList<MemoryDocumentChunk> parts;
        try
        {
            parts = chunker.Chunk(file.Title, file.Content);
        }
        catch (MemoryDocumentChunkRefusedException exception)
        {
            throw new MemorySeedDocumentRefusedException(file.Source, exception);
        }

        var chunks = new List<MemorySeedChunk>();
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var embedding = await embeddingClient.CreateEmbeddingAsync(
                new EmbeddingRequest(
                    part.Text, route.Model, "memory-seed:" + file.Source, EmbeddingInputKind.Passage, route.ProviderId),
                cancellationToken);
            chunks.Add(new MemorySeedChunk(
                MemorySeedFileLoader.DeterministicId(
                    item.Id + ":" + part.Position + ":" + embedding.Provider + ":" + embedding.Model + ":" + embedding.Vector.Count),
                part.Position,
                part.Text,
                ComputeSha256Hex(part.Text),
                embedding.Provider,
                embedding.Model,
                embedding.Vector.Count,
                embedding.Vector,
                part.HeadingPath));
        }

        return new MemorySeedEntry(item, chunks);
    }

    private static MemorySeedItem CreateItem(MemorySeedFile file, MemorySeedOptions settings, string contentHash) =>
        new(MemorySeedFileLoader.DeterministicId(
                settings.TenantId + ":" + settings.Owner + ":" + file.Source + ":seed-v3"),
            settings.TenantId, file.Kind, file.Source, file.Title, file.Content, contentHash,
            Version: 1, file.Tags, file.ServiceName, file.Component, file.ReleaseName);

    private static string ComputeSha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
