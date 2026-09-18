namespace IncidentCompass.Application.Memory;

/// <summary>
/// Defines the application-owned port for a relevance judge: a cross-encoder that reads one query
/// together with one candidate passage and says how relevant the passage is to that query.
/// </summary>
/// <remarks>
/// <para>
/// This is not an embedding port. There is no vector, no width, no pooling and no query or passage
/// prefix: a judge answers about one pair and nothing it returns can be stored, compared across
/// queries or indexed. A higher score means more relevant, on whatever scale the adapter's model
/// uses, so scores order the candidates of one call and mean nothing between calls.
/// </para>
/// <para>
/// Implementations must be safe to use from multiple concurrent request scopes and must not retain
/// mutable request state between calls. Provider SDK, runtime and file failures must be normalized
/// to <see cref="MemoryRelevanceJudgeException" /> before crossing this boundary, and no
/// implementation may log the query, a candidate or a score.
/// </para>
/// </remarks>
public interface IMemoryRelevanceJudge
{
    /// <summary>
    /// Scores every candidate against <paramref name="query" />, returning one score per candidate in
    /// the order the candidates were given. An empty candidate list returns an empty result and does
    /// no work.
    /// </summary>
    /// <remarks>
    /// Implementations must honor cancellation between candidates as well as inside one, because a
    /// worker's lease ownership can change while a long candidate set is being scored. Throw
    /// <see cref="OperationCanceledException" /> only when the caller-provided token is canceled;
    /// every other interruption must surface as <see cref="MemoryRelevanceJudgeException" /> so
    /// attempt accounting stays deterministic.
    /// </remarks>
    Task<IReadOnlyList<float>> ScoreAsync(
        string query,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken);
}
