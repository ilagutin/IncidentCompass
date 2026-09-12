namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// Reads the metadata-only corpus status for the configured memory seed tenant and owner.
/// </summary>
public interface IMemoryCorpusStatusReader
{
    Task<MemoryCorpusSnapshot> GetAsync(CancellationToken cancellationToken);
}
