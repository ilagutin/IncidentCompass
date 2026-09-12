using System.Text.RegularExpressions;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Keeps the production overlay's database credentials safe by construction rather than by
/// coincidence.
///
/// <c>docker-compose.yml</c> gives <c>POSTGRES_DB</c>, <c>POSTGRES_USER</c> and
/// <c>POSTGRES_PASSWORD</c> local demo fallbacks. A service that <c>compose.production.yml</c> does
/// not re-declare inherits them, so the only thing that can keep the demo values out of a production
/// run is the overlay requiring each name itself. <c>scripts/production-preflight.ps1</c> rejects the
/// demo password too, but it inspects the operator's environment file, not the compose files: an edit
/// that dropped a <c>:?</c> requirement, replaced it with a <c>:-</c> default or pasted the demo
/// password into the overlay would leave every preflight check passing. These assertions read the
/// files themselves, need no Docker and no environment file, and fail on exactly that edit.
/// </summary>
public sealed class ProductionComposeCredentialTests
{
    private const string DemoPassword = "incidentcompass_dev_password";

    private static readonly string[] CredentialVariables =
    [
        "POSTGRES_DB",
        "POSTGRES_USER",
        "POSTGRES_PASSWORD"
    ];

    /// <summary>
    /// States the premise the other assertions defend. If the demo stack ever stops supplying
    /// fallbacks, the overlay's declarations are no longer load-bearing and this whole file should be
    /// reconsidered rather than silently kept green.
    /// </summary>
    [Fact]
    public void LocalDemoCompose_SuppliesTheCredentialFallbacksTheOverlayMustDisplace()
    {
        var demoCompose = ReadComposeFile("docker-compose.yml");

        foreach (var variable in CredentialVariables)
        {
            Assert.Contains("${" + variable + ":-", demoCompose, StringComparison.Ordinal);
        }

        Assert.Contains(DemoPassword, demoCompose, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionOverlayPostgresService_RequiresEveryCredentialVariableItself()
    {
        var postgresService = ReadServiceBlock("compose.production.yml", "postgres");

        foreach (var variable in CredentialVariables)
        {
            // A failure here means the 'postgres' service stopped requiring this variable itself.
            // Restore it as 'NAME: ${NAME:?message}' or as '- NAME=${NAME:?message}': the ':?' is the
            // whole guarantee, because without it the demo default in 'docker-compose.yml' reaches the
            // database server.
            Assert.Matches(RequiredDeclarationPattern(variable), postgresService);
        }
    }

    [Fact]
    public void ProductionOverlay_NeverReadsACredentialVariableWithoutRequiringIt()
    {
        var overlay = ReadComposeFile("compose.production.yml");

        foreach (var variable in CredentialVariables)
        {
            Assert.DoesNotContain("${" + variable + ":-", overlay, StringComparison.Ordinal);
            Assert.DoesNotContain("${" + variable + "}", overlay, StringComparison.Ordinal);

            // The brace-less '$NAME' read is the third way to get an unrequired value, and neither
            // check above sees it. Rewrite any match as '${NAME:?message}'.
            Assert.DoesNotMatch(BraceLessReadPattern(variable), overlay);
        }

        Assert.DoesNotContain(DemoPassword, overlay, StringComparison.Ordinal);
    }

    /// <summary>
    /// Matches one safe declaration of the named variable in either shape Compose accepts for an
    /// environment entry: the mapping form <c>NAME: ${NAME:?message}</c> and the list form
    /// <c>- NAME=${NAME:?message}</c>, with the key or the value optionally quoted. Both shapes are
    /// equally safe, so pinning the file to one of them would fail a rewrite that changed nothing
    /// about the guarantee.
    ///
    /// What the pattern does not bend on is the <c>:?</c>. The interpolation must name the same
    /// variable and must require it, so a <c>:-</c> default, a bare <c>${NAME}</c> read, a brace-less
    /// read and a literal value all fail to match. That is the only thing being asserted.
    /// </summary>
    private static Regex RequiredDeclarationPattern(string variable) => new(
        "^[ \\t]*(?:-[ \\t]*)?[\"']?" + Regex.Escape(variable) + "[\"']?[ \\t]*(?::|=)[ \\t]*[\"']?" +
        "\\$\\{" + Regex.Escape(variable) + ":\\?",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);

    /// <summary>
    /// Matches the brace-less <c>$NAME</c> read. Compose expands it like <c>${NAME}</c>, but the form
    /// has no room for a <c>:?</c> requirement, so it is always an unrequired read. The lookahead keeps
    /// it from firing on a longer name that merely starts with this one.
    /// </summary>
    private static Regex BraceLessReadPattern(string variable) => new(
        "\\$" + Regex.Escape(variable) + "(?![A-Za-z0-9_])",
        RegexOptions.CultureInvariant);

    private static string ReadComposeFile(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRootLocator.Find(), fileName));

    /// <summary>
    /// Returns the body of one service declaration. A service body is indented by four spaces or
    /// more, so the block ends at the next non-blank line that is not, which is either the next
    /// service or the next top-level key.
    /// </summary>
    private static string ReadServiceBlock(string fileName, string serviceName)
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRootLocator.Find(), fileName));
        var header = "  " + serviceName + ":";
        var start = Array.IndexOf(lines, header);

        Assert.True(
            start >= 0,
            $"'{fileName}' declares no '{serviceName}' service, so the credential guarantee this " +
            "asserts cannot be checked. Update this test with the declaration that replaced it.");

        var body = new List<string>();
        for (var index = start + 1; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.Trim().Length > 0 && !line.StartsWith("    ", StringComparison.Ordinal))
            {
                break;
            }

            body.Add(line);
        }

        return string.Join('\n', body);
    }
}
