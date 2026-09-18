using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// One process owns the in-process models, and it is the Worker: it composes the embedding client and
/// the relevance judge, runs the memory seed and resync pass and serves the <c>memory status</c>,
/// <c>memory rebuild</c> and <c>memory model</c> commands. The Api reads corpus status and health and
/// runs no model, so its source must not name the embedding or judge adapters, either port, the seam
/// that composes them or the corpus and model commands. A plain text scan is enough here, because each
/// token is only ever written for one of those purposes.
/// </summary>
public sealed class ModelHostArchitectureTests
{
    private static readonly string[] ForbiddenApiTokens =
    [
        "IncidentCompass.Infrastructure.Embeddings",
        "IncidentCompass.Infrastructure.EmbeddingModels",
        "IncidentCompass.Infrastructure.Relevance",
        "IEmbeddingClient",
        "IMemoryRelevanceJudge",
        "AddEmbeddingHost",
        "AddLocalOnnxRelevanceJudge",
        "LocalOnnxRelevanceJudgeOptions",
        "LocalOnnxRelevanceJudgeClient",
        "LocalOnnxRelevanceJudgeRuntime",
        "LocalOnnxInstalledRelevanceJudgeReader",
        "MemoryCorpusCommand",
        "MemoryModelCommand",
        "RelevanceJudgeModelSection"
    ];

    [Fact]
    public void ApiSource_DoesNotComposeOrCallTheEmbeddingModel()
    {
        var repositoryRoot = RepositoryRootLocator.Find();
        var apiDirectory = Path.Combine(repositoryRoot, "src", "IncidentCompass.Api");
        Assert.True(Directory.Exists(apiDirectory), $"{apiDirectory} does not exist.");
        var failures = new List<string>();
        var sourceFiles = EnumerateSourceFiles(apiDirectory).ToArray();
        Assert.NotEmpty(sourceFiles);

        foreach (var sourcePath in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(repositoryRoot, sourcePath).Replace('\\', '/');
            var content = File.ReadAllText(sourcePath);
            failures.AddRange(FindForbiddenTokens(content)
                .Select(token =>
                    $"{relativePath} contains {token}; only the Worker composes or calls the in-process models."));
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// The scan is only worth as much as its token list, so each token is also checked against a line
    /// that names it. A token dropped from the list, or misspelled into one nothing can match, fails
    /// here rather than leaving the scan quietly green.
    /// </summary>
    [Theory]
    [InlineData("using IncidentCompass.Infrastructure.Relevance.LocalOnnx;")]
    [InlineData("services.GetRequiredService<IMemoryRelevanceJudge>();")]
    [InlineData("services.AddLocalOnnxRelevanceJudge(configuration);")]
    [InlineData("IOptions<LocalOnnxRelevanceJudgeOptions> options")]
    [InlineData("services.TryAddSingleton<IMemoryRelevanceJudge, LocalOnnxRelevanceJudgeClient>();")]
    [InlineData("var runtime = new LocalOnnxRelevanceJudgeRuntime(options);")]
    [InlineData("var lookup = await new LocalOnnxInstalledRelevanceJudgeReader(a, b, c).ReadAsync(token);")]
    [InlineData("await RelevanceJudgeModelSection.RunAsync(services);")]
    [InlineData("services.AddEmbeddingHost(configuration);")]
    public void ForbiddenTokens_AreRefusedWhereverTheyAppear(string apiSourceLine)
    {
        Assert.NotEmpty(FindForbiddenTokens(apiSourceLine));
    }

    private static IEnumerable<string> FindForbiddenTokens(string content) =>
        ForbiddenApiTokens.Where(token => content.Contains(token, StringComparison.Ordinal));

    private static IEnumerable<string> EnumerateSourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
}
