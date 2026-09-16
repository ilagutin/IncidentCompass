namespace IncidentCompass.Application.Memory;

/// <summary>
/// The stable codes of an embedding route the in-process local model cannot serve by configuration.
/// The seed pass records them when it publishes nothing, and the local adapter refuses an embedding
/// call with them, so a triage job whose <c>memory_search</c> meets one stores the same code the
/// corpus status reports. Both are operator-fixable states, not provider outages.
/// </summary>
internal static class MemoryEmbeddingModelErrorCodes
{
    /// <summary>A local model is installed, but it is not the model the request or route names.</summary>
    public const string Mismatch = "memory_embedding_model_mismatch";

    /// <summary>No usable local model is installed: none was installed, or the install failed or no longer verifies.</summary>
    public const string Unavailable = "memory_embedding_model_unavailable";
}
