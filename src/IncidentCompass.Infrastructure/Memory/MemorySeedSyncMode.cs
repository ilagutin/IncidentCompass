namespace IncidentCompass.Infrastructure.Memory;

/// <summary>
/// How much of the corpus one synchronization pass is prepared to re-embed.
/// </summary>
internal enum MemorySeedSyncMode
{
    /// <summary>
    /// Embeds only the files whose reviewed content changed, and refuses to publish at all when
    /// the configured embedding route no longer matches the corpus. This is the startup and
    /// runtime-resync path: it never spends a whole corpus of provider calls on its own.
    /// </summary>
    Incremental = 0,

    /// <summary>
    /// Embeds every reviewed file under the configured route and publishes the result as a new
    /// generation. This is the operator-driven path, and the only one that can move a corpus from
    /// one vector space to another.
    /// </summary>
    Rebuild = 1
}
