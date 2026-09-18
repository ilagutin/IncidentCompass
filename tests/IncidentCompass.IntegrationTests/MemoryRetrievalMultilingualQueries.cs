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

    public const string Version3 = "memory-retrieval-multilingual-queries-v3";

    /// <summary>Loads the renderings of the version 1 corpus queries.</summary>
    public static MemoryRetrievalMultilingualQueries Load(string repoRoot) =>
        LoadFile(repoRoot, "multilingual-queries-v1.json");

    /// <summary>Loads the renderings of the version 2 corpus queries.</summary>
    public static MemoryRetrievalMultilingualQueries LoadV2(string repoRoot) =>
        LoadFile(repoRoot, "multilingual-queries-v2.json");

    /// <summary>
    /// Loads the renderings of the version 3 corpus queries, each of which also carries the fault
    /// message of its source query's signal in the rendering's own language.
    /// </summary>
    public static MemoryRetrievalMultilingualQueries LoadV3(string repoRoot) =>
        LoadFile(repoRoot, "multilingual-queries-v3.json");

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
            (2, Version2, MemoryRetrievalBenchmarkCorpus.Version2) or
            (3, Version3, MemoryRetrievalBenchmarkCorpus.Version3);
        if (!supported || Queries.Count == 0)
        {
            throw new InvalidOperationException("Unsupported multilingual query contract.");
        }

        foreach (var entry in Queries)
        {
            var valid = SchemaVersion >= 3
                ? !string.IsNullOrWhiteSpace(entry.ErrorMessage)
                : entry.ErrorMessage is null;
            if (!valid)
            {
                throw new InvalidOperationException(
                    "Multilingual query '" + entry.Id + "' must carry an error message from schema 3 on and none before it.");
            }
        }
    }

    /// <summary>
    /// The corpus with its queries replaced by the renderings in <paramref name="languages" />, each
    /// carrying the source query's labels, service name and category under the rendering's own id. The
    /// rendering supplies the text and, when the source query carries a signal, that signal's fault
    /// message, and nothing else: the signal's service name, error type, route and operation stay the
    /// English source's, so no judgement can drift between languages.
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
                return source with { Id = entry.Id, Text = entry.Text, Signal = RenderSignal(source, entry) };
            })
            .ToArray();
        return corpus with { Queries = queries };
    }

    private static MemoryRetrievalBenchmarkSignal? RenderSignal(
        MemoryRetrievalBenchmarkQuery source,
        MemoryRetrievalMultilingualQuery entry)
    {
        if (source.Signal is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(entry.ErrorMessage))
        {
            throw new InvalidOperationException(
                "Multilingual query '" + entry.Id + "' renders a query with a signal but carries no error message.");
        }

        return source.Signal with { ErrorMessage = entry.ErrorMessage };
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>
/// One rendering. <paramref name="ErrorMessage" /> is null before schema 3 and required from it on: it
/// is the rendering's own fault message, cut from <paramref name="Text" /> rather than written anew.
/// </summary>
public sealed record MemoryRetrievalMultilingualQuery(
    string Id,
    string SourceQueryId,
    string Language,
    string Text,
    string? ErrorMessage = null);
