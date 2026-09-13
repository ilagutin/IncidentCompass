using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The local model store and its install pass live in <c>src/IncidentCompass.Infrastructure/EmbeddingModels</c>,
/// outside the directories <see cref="ModelGatewayLoggingGuardTests" /> keeps free of every output sink,
/// because they legitimately write files and log. That is safe only while they never see embedding
/// input or output. This tripwire fails when a file there names a port type that carries input text
/// or a vector, which is the moment that code has to move under the logging guard, or out of this
/// folder, rather than the guard being widened or this list being shortened.
/// </summary>
public sealed class EmbeddingModelStoreBoundaryTests
{
    private static readonly string[] EmbeddingPayloadTypeNames =
    [
        "EmbeddingRequest",
        "EmbeddingResponse",
        "EmbeddingInputKind"
    ];

    [Fact]
    public void EmbeddingModelStoreSources_NeverNameTheTypesThatCarryEmbeddingInputOrVectors()
    {
        var repositoryRoot = RepositoryRootLocator.Find();
        var storeDirectory = Path.Combine(repositoryRoot, "src", "IncidentCompass.Infrastructure", "EmbeddingModels");
        Assert.True(Directory.Exists(storeDirectory), $"{storeDirectory} does not exist.");
        var sourceFiles = EnumerateSourceFiles(storeDirectory).ToArray();
        Assert.NotEmpty(sourceFiles);
        var failures = new List<string>();

        foreach (var sourcePath in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(repositoryRoot, sourcePath).Replace('\\', '/');
            var content = File.ReadAllText(sourcePath);
            failures.AddRange(EmbeddingPayloadTypeNames
                .Where(typeName => content.Contains(typeName, StringComparison.Ordinal))
                .Select(typeName =>
                    $"{relativePath} names {typeName}. This folder writes files and logs outside the model" +
                    " gateway logging guard, so it must never handle embedding input or vectors."));
        }

        Assert.Empty(failures);
    }

    private static IEnumerable<string> EnumerateSourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
}
