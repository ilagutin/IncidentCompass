using System.Buffers;
using System.Text.RegularExpressions;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Guards the public reading path against naming a file or directory that does not exist. Published
/// release notes and the changelog are historical records of what shipped, so they are not reviewed.
/// </summary>
public sealed partial class DocumentationPathReferenceTests
{
    /// <summary>
    /// Repository-root directories whose contents documentation is allowed to name. A candidate whose
    /// first segment is outside this set is skipped, so media types such as <c>application/json</c> and
    /// generated output under <c>artifacts/</c> never reach the existence check.
    /// </summary>
    private static readonly HashSet<string> CheckedRootDirectories = new(
        [
            ".github",
            "config",
            "docs",
            "evaluations",
            "infra",
            "samples",
            "scripts",
            "src",
            "tests"
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// File extensions that mark a slash-free inline code span as a repository-root file rather than
    /// a settings name or a version fragment. Each one is an extension the reviewed documentation
    /// actually uses for a root file: <c>.md</c> for <c>README.md</c> and <c>CHANGELOG.md</c>,
    /// <c>.props</c> for <c>Directory.Packages.props</c>, and <c>.yml</c> for the four compose files.
    /// Extensions such as <c>.json</c>, <c>.cs</c> and <c>.sql</c> are deliberately absent: the
    /// documentation names those files by bare file name from directories other than the root.
    /// </summary>
    private static readonly HashSet<string> CheckedRootFileExtensions = new(
        [
            ".md",
            ".props",
            ".yml"
        ],
        StringComparer.Ordinal);

    private static readonly string[] ReviewedRootDocuments =
    [
        "AGENTS.md",
        "CLAUDE.md",
        "CONTRIBUTING.md",
        "README.md",
        "SECURITY.md"
    ];

    /// <summary>
    /// Characters that mean an inline code span is prose, a shell fragment or a placeholder rather
    /// than a literal repository path. Cached once because the scan runs for every inline code span
    /// in the reviewed documentation.
    /// </summary>
    private static readonly SearchValues<char> PathCandidateRejectedCharacters =
        SearchValues.Create([' ', '\t', '<', '>', '*', '$', '{', '}', '|', '?', '"', '\'']);

    [Fact]
    public void ReviewedDocumentation_NamesOnlyPathsThatExist()
    {
        var repositoryRoot = RepositoryRootLocator.Find();
        var checkedCandidates = new List<string>();
        var failures = new List<string>();

        foreach (var documentPath in ReviewedDocuments(repositoryRoot))
        {
            var documentName = Path.GetRelativePath(repositoryRoot, documentPath).Replace('\\', '/');
            foreach (Match match in InlineCodeSpanRegex().Matches(File.ReadAllText(documentPath)))
            {
                var candidate = match.Groups["span"].Value;
                if (!IsRepositoryPathCandidate(candidate))
                {
                    continue;
                }

                checkedCandidates.Add(candidate);
                if (!PathExists(repositoryRoot, candidate))
                {
                    failures.Add($"{documentName} names {candidate}, which does not exist.");
                }
            }
        }

        // A broken pattern or an empty document set would otherwise report a clean run, so the
        // candidate set has to be non-empty before the absence of failures means anything.
        Assert.NotEmpty(checkedCandidates);
        Assert.Empty(failures);
    }

    [Fact]
    public void ReviewedDocumentation_LinksOnlyToTargetsThatExist()
    {
        var repositoryRoot = RepositoryRootLocator.Find();
        var checkedTargets = new List<string>();
        var failures = new List<string>();

        foreach (var documentPath in ReviewedDocuments(repositoryRoot))
        {
            var documentName = Path.GetRelativePath(repositoryRoot, documentPath).Replace('\\', '/');
            var documentDirectory = Path.GetDirectoryName(documentPath)!;
            foreach (Match match in MarkdownLinkTargetRegex().Matches(File.ReadAllText(documentPath)))
            {
                if (!TryGetLocalLinkTarget(match.Groups["target"].Value, out var target))
                {
                    continue;
                }

                checkedTargets.Add(target);
                if (!PathExists(documentDirectory, target) && !PathExists(repositoryRoot, target))
                {
                    failures.Add($"{documentName} links to {target}, which does not exist.");
                }
            }
        }

        // Same guard as above: no local link targets at all means the scan, not the documentation,
        // is broken.
        Assert.NotEmpty(checkedTargets);
        Assert.Empty(failures);
    }

    private static IEnumerable<string> ReviewedDocuments(string repositoryRoot)
    {
        foreach (var name in ReviewedRootDocuments)
        {
            yield return Path.Combine(repositoryRoot, name);
        }

        var documentationDirectory = Path.Combine(repositoryRoot, "docs");
        var documentationFiles = Directory
            .EnumerateFiles(documentationDirectory, "*.md")
            .Order(StringComparer.Ordinal);

        foreach (var path in documentationFiles)
        {
            if (Path.GetFileName(path).StartsWith("release-notes-", StringComparison.Ordinal))
            {
                continue;
            }

            yield return path;
        }
    }

    private static bool IsRepositoryPathCandidate(string candidate)
    {
        if (candidate.StartsWith('/') ||
            candidate.StartsWith("http", StringComparison.Ordinal) ||
            candidate.AsSpan().ContainsAny(PathCandidateRejectedCharacters))
        {
            return false;
        }

        // A slash-free span names a repository-root file only when it carries one of the extensions
        // the documentation uses for root files. Settings names such as MaxTurns and POSTGRES_USER
        // have no extension, and version fragments such as 0.2.0 have one outside the set, so both
        // stay out of the existence check.
        if (!candidate.Contains('/', StringComparison.Ordinal))
        {
            return CheckedRootFileExtensions.Contains(Path.GetExtension(candidate));
        }

        return CheckedRootDirectories.Contains(candidate.Split('/')[0]);
    }

    private static bool TryGetLocalLinkTarget(string rawTarget, out string target)
    {
        target = string.Empty;
        if (rawTarget.StartsWith('#') ||
            rawTarget.StartsWith("http", StringComparison.Ordinal) ||
            rawTarget.StartsWith("mailto:", StringComparison.Ordinal))
        {
            return false;
        }

        target = rawTarget.Split('#')[0];
        return target.Length > 0;
    }

    private static bool PathExists(string basePath, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(basePath, relativePath.TrimEnd('/')));
        return File.Exists(fullPath) || Directory.Exists(fullPath);
    }

    /// <summary>
    /// Matches one single-line inline code span. Excluding carriage return and line feed from the
    /// span keeps an unpaired backtick, such as a fenced code block marker, from swallowing the rest
    /// of the document.
    /// </summary>
    [GeneratedRegex(@"`(?<span>[^`\r\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCodeSpanRegex();

    /// <summary>
    /// Matches the target of a markdown inline link: the run after <c>](</c> up to the first
    /// whitespace or the closing parenthesis. An optional link title follows that run, so the
    /// trailing group consumes it up to the closing parenthesis. Without that group a titled link
    /// would produce no match at all and its target would go unchecked; the title itself stays
    /// outside the captured path.
    /// </summary>
    [GeneratedRegex(@"\]\((?<target>[^)\s]+)(?:\s+[^)]*)?\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLinkTargetRegex();
}
