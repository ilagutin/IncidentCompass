namespace IncidentCompass.Application.Memory;

/// <summary>
/// One reconciliation's worth of seed state for a single owner and tenant.
/// </summary>
/// <remarks>
/// <c>Identity</c> is the embedding route this generation was built under. It is recorded with the
/// generation inside the publishing transaction, so what became current and what vector space it
/// is in are one fact rather than two that can drift apart.
/// </remarks>
internal sealed record MemorySeedCorpus(
    string TenantId,
    string Owner,
    Guid Generation,
    MemoryCorpusIdentity Identity,
    IReadOnlySet<string> ObservedSourcePrefixes,
    IReadOnlyList<MemorySeedEntry> Entries);
