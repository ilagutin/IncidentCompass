using System.Text.RegularExpressions;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// The only branch names this adapter will put in a URL.
/// </summary>
/// <remarks>
/// It is an allowlist rather than an escape. A name is used as a path segment sequence, and a name
/// that needed escaping would be a name that could change which resource the request addresses; the
/// answer to that is not to encode it more carefully but to refuse anything outside a small alphabet.
/// The names this product creates are derived from a report id and pass trivially; the configured base
/// branch is an operator string and is checked at startup and again here.
/// </remarks>
internal static partial class GitReferenceName
{
    public const int MaximumCharacters = 100;

    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaximumCharacters } &&
        NameRegex().IsMatch(value) &&
        !value.Contains("..", StringComparison.Ordinal) &&
        !value.Contains("//", StringComparison.Ordinal) &&
        !value.EndsWith(".lock", StringComparison.Ordinal) &&
        !value.EndsWith('/');

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();
}
