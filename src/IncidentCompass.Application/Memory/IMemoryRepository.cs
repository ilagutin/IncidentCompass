namespace IncidentCompass.Application.Memory;

internal interface IMemoryRepository
{
    Task<IReadOnlyList<MemorySearchMatch>> SearchAsync(
        MemorySearchRequest request,
        CancellationToken cancellationToken);

    Task<bool> SeedItemExistsAsync(
        string owner,
        MemorySeedItem item,
        CancellationToken cancellationToken);

    Task ReconcileSeedCorpusAsync(
        MemorySeedCorpus corpus,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads what one owner's corpus currently holds, so a route change can be reported before any
    /// embedding is requested.
    /// </summary>
    Task<MemoryCorpusInventory> GetCorpusInventoryAsync(
        string tenantId,
        string owner,
        CancellationToken cancellationToken);
}
