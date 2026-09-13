using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// One process owns the embedding model, and it is the Worker: it composes the embedding client, runs
/// the memory seed and resync pass and serves the <c>memory status</c> and <c>memory rebuild</c>
/// commands. The Api reads corpus status and health and embeds nothing, so its source must not name
/// the embedding adapters, the embedding port, the seam that composes them or the corpus command. A
/// plain text scan is enough here, because each token is only ever written for one of those purposes.
/// </summary>
public sealed class ModelHostArchitectureTests
{
    private static readonly string[] ForbiddenApiTokens =
    [
        "IncidentCompass.Infrastructure.Embeddings",
        "IEmbeddingClient",
        "AddEmbeddingHost",
        "MemoryCorpusCommand"
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
            failures.AddRange(ForbiddenApiTokens
                .Where(token => content.Contains(token, StringComparison.Ordinal))
                .Select(token =>
                    $"{relativePath} contains {token}; only the Worker composes or calls the embedding model."));
        }

        Assert.Empty(failures);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
}
