using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Finds every triage configuration file the repository carries, so the schema-dialect and
/// published-schema guarantees apply to all of them instead of to one hardcoded path. Test fixture
/// configurations are loaded by the same loader and validated by the same validator as the shipped
/// one, so a divergence between them is a defect no matter which file it lives in.
/// </summary>
internal static class TriageConfigurationFileLocator
{
    private const string ConfigurationFileName = "incidentcompass.config.json";

    /// <summary>
    /// Enumerates every triage configuration in the repository, ordered so the enumeration is stable.
    /// Build output and hidden directories are skipped: build output holds copies of files that are
    /// already covered at their source path, and hidden directories are not part of the repository's
    /// tracked content.
    /// </summary>
    public static IReadOnlyList<string> FindAll()
    {
        var root = RepositoryRootLocator.Find();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System
        };
        return Directory
            .EnumerateFiles(root, ConfigurationFileName, options)
            .Where(path => !IsExcluded(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The shipped configuration, which is the one the published documentation and the container
    /// images describe.
    /// </summary>
    public static string Shipped() =>
        Path.Combine(RepositoryRootLocator.Find(), "config", ConfigurationFileName);

    /// <summary>
    /// Guards a discovery result against both emptiness and truncation. The shipped configuration is
    /// the one the published documentation and the container images describe; the fixture
    /// configurations under <c>tests/</c> are the ones a new exclusion rule or a renamed fixture
    /// directory would silently drop, which would leave a green test that only ever looked at the
    /// shipped file. Asserting the shipped path alone cannot tell a complete result from a truncated
    /// one, so both halves of the discovery are asserted here.
    /// </summary>
    public static void AssertDiscoveryCoversShippedAndFixtureConfigurations(
        IReadOnlyList<string> configurationPaths)
    {
        Assert.Contains(Shipped(), configurationPaths);

        var root = RepositoryRootLocator.Find();
        Assert.True(
            configurationPaths.Any(path => IsUnderTestsDirectory(root, path)),
            "Configuration discovery found no test fixture configuration under 'tests/', so it has " +
            "been truncated by an exclusion rule or by a renamed fixture directory and now covers " +
            "only: " + string.Join(", ", configurationPaths));
    }

    /// <summary>
    /// Resolves one <c>ref:</c> reference of the given configuration to a file on disk. A reference
    /// resolves against the configuration's own directory, which is what the loader does. The single
    /// exception is the evaluation configuration, which carries no instructions or schemas of its own
    /// because <c>compose.evaluation.yml</c> mounts it into the shipped configuration directory, so
    /// its references resolve against <c>config/</c>.
    /// </summary>
    public static string ResolveReference(string configurationPath, string reference)
    {
        const string prefix = "ref:";
        Assert.StartsWith(prefix, reference, StringComparison.Ordinal);
        var relativePath = reference[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        var referenceDirectory = IsDeployedIntoShippedConfigurationDirectory(configurationPath)
            ? Path.Combine(RepositoryRootLocator.Find(), "config")
            : Path.GetDirectoryName(configurationPath)!;
        var resolvedPath = Path.Combine(referenceDirectory, relativePath);

        Assert.True(
            File.Exists(resolvedPath),
            $"Configuration '{configurationPath}' references '{reference}', which does not exist at " +
            $"'{resolvedPath}'. Every configuration except the deployed evaluation one must carry the " +
            "file it references beside itself.");
        return resolvedPath;
    }

    /// <summary>
    /// The evaluation configuration is named here rather than detected by a missing directory or a
    /// missing file, because either of those tests would let a deleted or renamed fixture reference
    /// fall through to the shipped file of the same name: the caller would then validate
    /// <c>config/schemas/source.json</c> while reporting the fixture it thought it read. Naming the
    /// one configuration that is deployed elsewhere keeps every other missing reference a loud
    /// failure. A second configuration deployed the same way fails loudly here first, which is the
    /// point at which it should be added.
    /// </summary>
    private static bool IsDeployedIntoShippedConfigurationDirectory(string configurationPath) =>
        string.Equals(
            Path.GetFullPath(configurationPath),
            Path.GetFullPath(Path.Combine(
                RepositoryRootLocator.Find(),
                "evaluations",
                "triage",
                ConfigurationFileName)),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsUnderTestsDirectory(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => string.Equals(segment, "tests", StringComparison.OrdinalIgnoreCase));

    private static bool IsExcluded(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        foreach (var segment in relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.StartsWith('.') ||
                string.Equals(segment, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "obj", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(segment, "node_modules", StringComparison.Ordinal) ||
                string.Equals(segment, "TestResults", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
