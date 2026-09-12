namespace IncidentCompass.TestSupport;

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
    /// The evaluation configuration, which the triage evaluation stack loads from this repository path
    /// and mounts at the same path inside its images.
    /// </summary>
    public static string Evaluation() =>
        Path.Combine(RepositoryRootLocator.Find(), "evaluations", "triage", ConfigurationFileName);

    /// <summary>
    /// Guards a discovery result against both emptiness and truncation. Every configuration named by
    /// path elsewhere in the repository is asserted by name: the shipped one the published
    /// documentation and the container images describe, and the evaluation one the evaluation stack
    /// loads. The fixture configurations under <c>tests/</c> are asserted by presence instead, because
    /// they are added and renamed freely.
    ///
    /// Asserting the shipped path alone cannot tell a complete result from a truncated one: a new
    /// exclusion rule, a moved file or a renamed directory can drop one of the others and leave a
    /// green test that only ever looked at the shipped file. That is the regression this exists to
    /// catch, so each part of the discovery is asserted separately. No total count is asserted, so an
    /// added configuration does not fail this guard for no reason.
    /// </summary>
    public static void AssertDiscoveryCoversEveryKnownConfiguration(
        IReadOnlyList<string> configurationPaths)
    {
        AssertDiscovered(configurationPaths, Shipped(), "the shipped configuration");
        AssertDiscovered(configurationPaths, Evaluation(), "the evaluation configuration");

        var root = RepositoryRootLocator.Find();
        Assert.True(
            configurationPaths.Any(path => IsUnderTestsDirectory(root, path)),
            "Configuration discovery found no test fixture configuration under 'tests/', so it has " +
            "been truncated by an exclusion rule or by a renamed fixture directory and now covers " +
            "only: " + string.Join(", ", configurationPaths));
    }

    /// <summary>
    /// Resolves one <c>ref:</c> reference of the given configuration to a file on disk against the
    /// configuration's own directory, which is exactly what
    /// <c>FileTriageConfigurationRepository</c> does. Every configuration in the repository
    /// therefore resolves here the same way it resolves at runtime, with no per-file exception: a
    /// configuration that shares another directory's instructions says so in its own reference.
    /// </summary>
    public static string ResolveReference(string configurationPath, string reference)
    {
        const string prefix = "ref:";
        Assert.StartsWith(prefix, reference, StringComparison.Ordinal);
        var relativePath = reference[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        var resolvedPath = Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(configurationPath)!, relativePath));

        Assert.True(
            File.Exists(resolvedPath),
            $"Configuration '{configurationPath}' references '{reference}', which does not exist at " +
            $"'{resolvedPath}'. A reference must resolve against the configuration's own directory, " +
            "because that is the only directory the loader looks in.");
        return resolvedPath;
    }

    private static void AssertDiscovered(
        IReadOnlyList<string> configurationPaths,
        string expectedPath,
        string description)
    {
        Assert.True(
            configurationPaths.Any(path => string.Equals(path, expectedPath, StringComparison.Ordinal)),
            $"Configuration discovery did not return {description} at '{expectedPath}', so it has been " +
            "truncated by an exclusion rule or by a moved file and now covers only: " +
            string.Join(", ", configurationPaths));
    }

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
