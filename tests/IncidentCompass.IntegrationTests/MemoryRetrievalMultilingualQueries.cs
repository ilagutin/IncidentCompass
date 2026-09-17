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

    public const string Version1 = "memory-retrieval-multilingual-queries-v1";

    public const string Version2 = "memory-retrieval-multilingual-queries-v2";

    /// <summary>Loads the renderings of the version 1 corpus queries.</summary>
    public static MemoryRetrievalMultilingualQueries Load(string repoRoot) =>
        LoadFile(repoRoot, "multilingual-queries-v1.json");

    /// <summary>Loads the renderings of the version 2 corpus queries.</summary>
    public static MemoryRetrievalMultilingualQueries LoadV2(string repoRoot) =>
        LoadFile(repoRoot, "multilingual-queries-v2.json");

    private static MemoryRetrievalMultilingualQueries LoadFile(string repoRoot, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        var path = Path.Combine(
            repoRoot,
            "tests",
            "IncidentCompass.IntegrationTests",
            "Fixtures",
            "MemoryRetrieval",
            fileName);
        var queries = JsonSerializer.Deserialize<MemoryRetrievalMultilingualQueries>(
            File.ReadAllText(path),
            SerializerOptions) ?? throw new InvalidOperationException("Multilingual query fixture is empty.");
        queries.Validate();
        return queries;
    }

    /// <summary>
    /// Each fixture version renders exactly one corpus version, so a rendering can never be pooled onto
    /// a corpus whose queries it does not cover.
    /// </summary>
    private void Validate()
    {
        var supported = (SchemaVersion, Version, CorpusVersion) is
            (1, Version1, MemoryRetrievalBenchmarkCorpus.Version1) or
            (2, Version2, MemoryRetrievalBenchmarkCorpus.Version2);
        if (!supported || Queries.Count == 0)
        {
            throw new InvalidOperationException("Unsupported multilingual query contract.");
        }
    }

    /// <summary>
    /// The corpus with its queries replaced by the renderings in <paramref name="languages" />, each
    /// carrying the source query's labels, service name and category under the rendering's own id. The
    /// rendering supplies the text and nothing else, so no judgement can drift between languages.
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
