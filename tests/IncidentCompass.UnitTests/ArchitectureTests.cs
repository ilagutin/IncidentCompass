using System.Xml.Linq;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

public sealed class ArchitectureTests
{
    private const string ApplicationNamespace = "IncidentCompass.Application";
    private const string ApplicationCoreNamespace = ApplicationNamespace + ".Core";
    private const string ApplicationCoreNamespacePrefix = ApplicationCoreNamespace + ".";

    private static readonly char[] DeclarationSeparators = [' ', '\t'];

    private static readonly string[] TypeDeclarationModifiers =
    [
        "public",
        "internal",
        "protected",
        "private",
        "file",
        "new",
        "sealed",
        "abstract",
        "static",
        "partial",
        "unsafe"
    ];

    private static readonly HashSet<string> ExactReferenceProjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "IncidentCompass.Domain",
        "IncidentCompass.Application"
    };

    private static readonly Dictionary<string, string[]> AllowedReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IncidentCompass.Domain"] = [],
        ["IncidentCompass.Application"] = ["IncidentCompass.Domain"],
        ["IncidentCompass.Infrastructure"] =
            [
                "IncidentCompass.Domain",
                "IncidentCompass.Application"
            ],
        ["IncidentCompass.Api"] =
            [
                "IncidentCompass.Application",
                "IncidentCompass.Infrastructure"
            ],
        ["IncidentCompass.Worker"] =
            [
                "IncidentCompass.Application",
                "IncidentCompass.Infrastructure"
            ],
        ["IncidentCompass.Tester"] = []
    };

    [Fact]
    public void SourceProjects_UseOnlyAllowedProjectReferences()
    {
        var projects = LoadSourceProjects();
        var failures = new List<string>();

        foreach (var project in projects.Values.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!AllowedReferences.TryGetValue(project.Name, out var allowedReferences))
            {
                failures.Add($"{project.Name} is not part of the approved source project matrix.");
                continue;
            }

            var allowed = allowedReferences.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in project.ProjectReferences.OrderBy(reference => reference, StringComparer.OrdinalIgnoreCase))
            {
                if (!allowed.Contains(reference))
                {
                    failures.Add($"{project.Name} must not reference {reference}.");
                }
            }

            if (ExactReferenceProjects.Contains(project.Name))
            {
                var expected = allowedReferences.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var actual = project.ProjectReferences.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"{project.Name} references [{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}].");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void ApplicationModuleNamespaces_MatchTargetModuleOwnership()
    {
        var projects = LoadSourceProjects();
        var rules = ModuleMembershipRules();
        var failures = new List<string>();

        foreach (var rule in rules)
        {
            if (!projects.TryGetValue(rule.ProjectName, out var project))
            {
                continue;
            }

            foreach (var filePath in EnumerateSourceFiles(project.Directory))
            {
                var relativePath = Path.GetRelativePath(project.Directory, filePath).Replace('\\', '/');
                if (relativePath.Equals("AssemblyInfo.cs", StringComparison.Ordinal))
                {
                    continue;
                }

                var declaredNamespace = ReadDeclaredNamespace(filePath);
                if (declaredNamespace is null)
                {
                    failures.Add($"{rule.ProjectName}/{relativePath} does not declare a namespace.");
                    continue;
                }

                if (!declaredNamespace.StartsWith(rule.NamespacePrefix, StringComparison.Ordinal))
                {
                    failures.Add(
                        $"{rule.ProjectName}/{relativePath} declares {declaredNamespace}; expected {rule.NamespacePrefix}.");
                }

                if (rule.AllowedTopLevelFolders.Count > 0 &&
                    !IsAllowedTopLevelFolder(relativePath, rule.AllowedTopLevelFolders))
                {
                    failures.Add(
                        $"{rule.ProjectName}/{relativePath} is outside allowed folders [{string.Join(", ", rule.AllowedTopLevelFolders)}].");
                }

                if (rule.AllowedTopLevelFolders.Count > 0 &&
                    TryGetExpectedFolderNamespace(rule, relativePath, out var expectedFolderNamespace) &&
                    !declaredNamespace.StartsWith(expectedFolderNamespace, StringComparison.Ordinal))
                {
                    failures.Add(
                        $"{rule.ProjectName}/{relativePath} declares {declaredNamespace}; expected {expectedFolderNamespace}.");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void SourceProjects_DoNotRedeclareDomainAlongsideApplication()
    {
        var failures = LoadSourceProjects().Values
            .Where(project =>
                project.ProjectReferences.Contains("IncidentCompass.Application", StringComparer.OrdinalIgnoreCase) &&
                project.ProjectReferences.Contains("IncidentCompass.Domain", StringComparer.OrdinalIgnoreCase))
            .Select(project =>
                $"{project.Name} declares IncidentCompass.Domain, which already arrives through IncidentCompass.Application.")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(failures);
    }

    [Fact]
    public void DomainProject_DoesNotDeclareApplicationPorts()
    {
        var domainDirectory = Path.Combine(RepositoryRootLocator.Find(), "src", "IncidentCompass.Domain");
        var filesWithInterfaces = EnumerateSourceFiles(domainDirectory)
            .Where(filePath => File.ReadLines(filePath).Any(IsInterfaceDeclaration))
            .Select(filePath => Path.GetRelativePath(domainDirectory, filePath).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(filesWithInterfaces);
    }

    /// <summary>
    /// The OpenAI-compatible model and embedding adapters are the real provider integrations. They
    /// bind to the gateway contracts in <c>IncidentCompass.Application.Core</c> and to nothing else
    /// in Application: an adapter that reaches into a feature folder has reached past the port it
    /// implements, and the primitive it wanted belongs in <c>Core</c> instead. The mock adapters are
    /// deliberately outside this rule - they script the orchestrator's own tool catalog, which is
    /// investigation behavior rather than provider behavior.
    /// The scan is over every occurrence of the namespace root in the file text rather than over
    /// plain <c>using</c> lines, so <c>using static</c>, a using alias and a fully qualified inline
    /// reference with no <c>using</c> at all are all caught. <c>using static</c> is idiomatic here:
    /// the mock script adapter next door already uses one.
    /// </summary>
    [Fact]
    public void OpenAiProviderAdapters_BindOnlyToApplicationCoreContracts()
    {
        var repositoryRoot = RepositoryRootLocator.Find();
        var infrastructureDirectory = Path.Combine(repositoryRoot, "src", "IncidentCompass.Infrastructure");
        var adapterDirectories = new[]
        {
            Path.Combine(infrastructureDirectory, "ModelGateway", "OpenAi"),
            Path.Combine(infrastructureDirectory, "Embeddings", "OpenAi")
        };
        var failures = new List<string>();

        foreach (var adapterDirectory in adapterDirectories)
        {
            Assert.True(Directory.Exists(adapterDirectory), $"{adapterDirectory} does not exist.");
            foreach (var sourcePath in EnumerateSourceFiles(adapterDirectory))
            {
                var relativePath = Path.GetRelativePath(repositoryRoot, sourcePath).Replace('\\', '/');
                failures.AddRange(EnumerateApplicationReferences(File.ReadAllText(sourcePath))
                    .Where(reference => !IsApplicationCoreReference(reference))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Select(reference =>
                        $"{relativePath} references {reference}; the OpenAI-compatible adapters may reference only {ApplicationCoreNamespace}.*."));
            }
        }

        Assert.Empty(failures);
    }

    // The markers below appear nowhere under `src/`, so this test passes by construction today. It
    // is a forward guard rather than a report of a past failure: Application is a pure orchestration
    // layer, and README lists MCP product surfaces as out of scope. Shelling out or taking an MCP
    // SDK dependency from Application would move process I/O and a provider protocol to the wrong
    // side of the port boundary, and this test fails the first change that tries it.
    [Fact]
    public void ApplicationProject_DoesNotReferenceExternalMcpSdkOrProcessIo()
    {
        var applicationDirectory = Path.Combine(RepositoryRootLocator.Find(), "src", "IncidentCompass.Application");
        var forbiddenMarkers = new[]
        {
            "ModelContextProtocol",
            "StdioClientTransport",
            "ProcessStartInfo",
            "System.Diagnostics.Process"
        };
        var failures = new List<string>();

        foreach (var projectPath in Directory.EnumerateFiles(applicationDirectory, "*.csproj"))
        {
            AddForbiddenMarkers(
                failures,
                "external MCP/process I/O",
                Path.GetRelativePath(RepositoryRootLocator.Find(), projectPath),
                File.ReadAllText(projectPath),
                forbiddenMarkers);
        }

        foreach (var sourcePath in EnumerateSourceFiles(applicationDirectory))
        {
            AddForbiddenMarkers(
                failures,
                "external MCP/process I/O",
                Path.GetRelativePath(RepositoryRootLocator.Find(), sourcePath),
                File.ReadAllText(sourcePath),
                forbiddenMarkers);
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void ApiAuthenticationDetails_DoNotLeakIntoApplicationOrDomain()
    {
        var forbiddenMarkers = new[]
        {
            "X-IncidentCompass-Key",
            "ApiKeyAuthOptions",
            "ApiKeyCredential",
            "FixedWindowRateLimiter",
            "Microsoft.AspNetCore.Authentication"
        };
        var failures = new List<string>();

        foreach (var projectName in new[] { "IncidentCompass.Application", "IncidentCompass.Domain" })
        {
            var projectDirectory = Path.Combine(RepositoryRootLocator.Find(), "src", projectName);
            foreach (var sourcePath in EnumerateSourceFiles(projectDirectory))
            {
                AddForbiddenMarkers(
                    failures,
                    "API authentication",
                    Path.GetRelativePath(RepositoryRootLocator.Find(), sourcePath),
                    File.ReadAllText(sourcePath),
                    forbiddenMarkers);
            }
        }

        Assert.Empty(failures);
    }

    private static Dictionary<string, SourceProject> LoadSourceProjects()
    {
        var sourceDirectory = Path.Combine(RepositoryRootLocator.Find(), "src");
        return Directory.EnumerateFiles(sourceDirectory, "*.csproj", SearchOption.AllDirectories)
            .Select(LoadSourceProject)
            .ToDictionary(project => project.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static SourceProject LoadSourceProject(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var references = document
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            // ProjectReference Include paths use Windows '\' separators; normalize to '/'
            // so Path APIs resolve them on Linux too (CI runs on ubuntu).
            .Select(include => include!.Replace('\\', '/'))
            .Select(include => Path.GetFullPath(Path.Combine(projectDirectory, include)))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SourceProject(Path.GetFileNameWithoutExtension(projectPath), projectDirectory, references);
    }

    private static IReadOnlyList<ModuleMembershipRule> ModuleMembershipRules() =>
        [
            new(
                "IncidentCompass.Application",
                "IncidentCompass.Application",
                ["Core", "Governance", "Intake", "Investigation", "Memory", "Notifications", "Observability", "SourceContext", "Tickets"])
        ];

    private static IEnumerable<string> EnumerateSourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string? ReadDeclaredNamespace(string filePath)
    {
        foreach (var line in File.ReadLines(filePath))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("namespace ", StringComparison.Ordinal))
            {
                continue;
            }

            return trimmed["namespace ".Length..]
                .Trim()
                .TrimEnd(';', '{')
                .Trim();
        }

        return null;
    }

    private static bool IsAllowedTopLevelFolder(string relativePath, IReadOnlyCollection<string> allowedFolders)
    {
        var firstSegment = relativePath.Split('/')[0];
        return firstSegment.Equals("Setup.cs", StringComparison.Ordinal) ||
            firstSegment.Equals("AssemblyInfo.cs", StringComparison.Ordinal) ||
            allowedFolders.Contains(firstSegment);
    }

    private static bool TryGetExpectedFolderNamespace(
        ModuleMembershipRule rule,
        string relativePath,
        out string expectedNamespace)
    {
        var firstSegment = relativePath.Split('/')[0];
        if (firstSegment.EndsWith(".cs", StringComparison.Ordinal) ||
            !rule.AllowedTopLevelFolders.Contains(firstSegment))
        {
            expectedNamespace = string.Empty;
            return false;
        }

        expectedNamespace = $"{rule.NamespacePrefix}.{firstSegment}";
        return true;
    }

    private static void AddForbiddenMarkers(
        List<string> failures,
        string category,
        string relativePath,
        string content,
        IReadOnlyCollection<string> markers)
    {
        foreach (var marker in markers)
        {
            if (content.Contains(marker, StringComparison.Ordinal))
            {
                failures.Add($"{relativePath} contains forbidden {category} marker {marker}.");
            }
        }
    }

    /// <summary>
    /// Yields every dotted path in the file text that starts at the <c>IncidentCompass.Application</c>
    /// namespace root, whatever syntax introduced it: a plain <c>using</c>, a <c>using static</c>, the
    /// right-hand side of a using alias, or a fully qualified reference written inline with no
    /// <c>using</c> at all. A match must begin and end on an identifier boundary, so
    /// <c>IncidentCompass.ApplicationHost</c> is not reported as the Application root.
    /// </summary>
    private static IEnumerable<string> EnumerateApplicationReferences(string content)
    {
        var index = content.IndexOf(ApplicationNamespace, StringComparison.Ordinal);
        while (index >= 0)
        {
            var boundary = index + ApplicationNamespace.Length;
            var end = boundary;
            while (end < content.Length && IsQualifiedNameCharacter(content[end]))
            {
                end++;
            }

            var startsAtBoundary = index == 0 || !IsQualifiedNameCharacter(content[index - 1]);
            var endsAtBoundary = boundary >= content.Length || !IsIdentifierCharacter(content[boundary]);
            if (startsAtBoundary && endsAtBoundary)
            {
                yield return content[index..end].TrimEnd('.');
            }

            index = content.IndexOf(ApplicationNamespace, end, StringComparison.Ordinal);
        }
    }

    private static bool IsApplicationCoreReference(string reference)
    {
        return reference.Equals(ApplicationCoreNamespace, StringComparison.Ordinal) ||
            reference.StartsWith(ApplicationCoreNamespacePrefix, StringComparison.Ordinal);
    }

    private static bool IsQualifiedNameCharacter(char value)
    {
        return IsIdentifierCharacter(value) || value == '.';
    }

    private static bool IsIdentifierCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }

    /// <summary>
    /// True when the line opens an <c>interface</c> declaration: leading attribute lists, then
    /// optional modifiers (including <c>new</c>), then the keyword, then either the declared name or
    /// the end of the line. Matching the bare substring <c>"interface "</c> instead reported the
    /// word wherever it appeared in a comment or a string, and missed three real declaration shapes:
    /// a keyword followed by a tab, a name wrapped onto the next line, and a line that starts with
    /// an attribute. Known gaps: an interface nested after an opening brace on the same line, and a
    /// declaration whose leading attribute contains an unbalanced <c>]</c> inside a string literal.
    /// </summary>
    private static bool IsInterfaceDeclaration(string line)
    {
        var tokens = StripLeadingAttributes(line).Split(
            DeclarationSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = 0;
        while (index < tokens.Length && TypeDeclarationModifiers.Contains(tokens[index], StringComparer.Ordinal))
        {
            index++;
        }

        if (index >= tokens.Length || !tokens[index].Equals("interface", StringComparison.Ordinal))
        {
            return false;
        }

        if (index + 1 >= tokens.Length)
        {
            // `public interface` with the declared name wrapped onto the next line.
            return true;
        }

        var declaredName = tokens[index + 1];
        return char.IsLetter(declaredName[0]) || declaredName[0] == '_';
    }

    /// <summary>
    /// Removes any attribute lists that open the line, so <c>[Obsolete] public interface IFoo</c> is
    /// tokenized from its first modifier. Brackets are matched by depth to keep a nested attribute
    /// argument such as <c>[Foo(new[] { 1 })]</c> intact; an attribute that never closes on this
    /// line yields no tokens at all rather than a partial declaration.
    /// </summary>
    private static string StripLeadingAttributes(string line)
    {
        var remainder = line.AsSpan().TrimStart();
        while (remainder.Length > 0 && remainder[0] == '[')
        {
            var depth = 0;
            var index = 0;
            while (index < remainder.Length)
            {
                if (remainder[index] == '[')
                {
                    depth++;
                }
                else if (remainder[index] == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                index++;
            }

            if (index >= remainder.Length)
            {
                return string.Empty;
            }

            remainder = remainder[(index + 1)..].TrimStart();
        }

        return remainder.ToString();
    }

    private sealed record SourceProject(string Name, string Directory, string[] ProjectReferences);

    private sealed record ModuleMembershipRule(
        string ProjectName,
        string NamespacePrefix,
        IReadOnlyCollection<string> AllowedTopLevelFolders);
}
