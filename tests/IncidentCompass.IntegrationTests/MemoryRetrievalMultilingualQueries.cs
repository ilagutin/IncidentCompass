using System.Text.Json;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Polish and Russian renderings of the corpus queries. An entry names its source query and carries
/// only its own text, so relevance labels and the service name always come from the English query
/// and cannot drift between languages.
/// </summary>
public sealed record MemoryRetrievalMultilingualQueries(
    int SchemaVersion,
    string Version,
    string CorpusVersion,
    IReadOnlyList<MemoryRetrievalMultilingualQuery> Queries)
{
    public const string Polish = "pl";

    public const string Russian = "ru";

    public static MemoryRetrievalMultilingualQueries Load(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var path = Path.Combine(
            repoRoot,
            "tests",
            "IncidentCompass.IntegrationTests",
            "Fixtures",
            "MemoryRetrieval",
            "multilingual-queries-v1.json");
        return JsonSerializer.Deserialize<MemoryRetrievalMultilingualQueries>(
            File.ReadAllText(path),
            SerializerOptions) ?? throw new InvalidOperationException("Multilingual query fixture is empty.");
    }

    /// <summary>
    /// The corpus with its queries replaced by the renderings in <paramref name="languages" />, each
    /// carrying the source query's labels and service name under the rendering's own id.
    /// </summary>
    public MemoryRetrievalBenchmarkCorpus ToCorpus(MemoryRetrievalBenchmarkCorpus corpus, params string[] languages)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        var sources = corpus.Queries.ToDictionary(static query => query.Id, StringComparer.Ordinal);
        var queries = Queries
            .Where(entry => languages.Contains(entry.Language, StringComparer.Ordinal))
            .Select(entry =>
            {
                var source = sources.TryGetValue(entry.SourceQueryId, out var found)
                    ? found
                    : throw new InvalidOperationException(
                        "Multilingual query '" + entry.Id + "' names an unknown source query.");
                return source with { Id = entry.Id, Text = entry.Text };
            })
            .ToArray();
        return corpus with { Queries = queries };
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed record MemoryRetrievalMultilingualQuery(
    string Id,
    string SourceQueryId,
    string Language,
    string Text);
