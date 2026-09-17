namespace IncidentCompass.Application.Memory;

internal static class MemorySearchLexicalFilter
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "api", "as", "by", "error", "event", "exception", "for",
        "from", "in", "into", "is", "it", "of", "on", "or", "post", "request", "requests",
        "service", "the", "this", "to", "while", "with"
    };

    public static IReadOnlyList<MemorySearchMatch> Apply(
        string query,
        IReadOnlyList<MemorySearchMatch> matches)
    {
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0)
        {
            return matches;
        }

        return matches
            .Where(match => Tokenize(match.Text).Overlaps(queryTokens))
            .ToArray();
    }

    /// <summary>
    /// Judges one candidate against one query over the query words that candidate could carry at all.
    /// For a query and a candidate written in the same script every counted word is eligible and the
    /// result is the plain half rule over every counted word.
    /// </summary>
    internal static MemorySearchLexicalSupport Evaluate(string query, string value)
    {
        var queryTokens = Tokenize(query);
        var valueTokens = Tokenize(value);
        var valueScripts = CountedScripts(valueTokens);
        var eligible = 0;
        var matched = 0;
        foreach (var token in queryTokens)
        {
            var script = WritingScriptClassifier.Classify(token);
            if (script != WritingScript.Neutral && !valueScripts.Contains(script))
            {
                continue;
            }

            eligible++;
            if (valueTokens.Contains(token))
            {
                matched++;
            }
        }

        return new MemorySearchLexicalSupport(queryTokens.Count, eligible, matched);
    }

    /// <summary>The writing systems the counted words of <paramref name="value" /> are written in.</summary>
    internal static IReadOnlySet<WritingScript> CountedScripts(string value) => CountedScripts(Tokenize(value));

    internal static double Coverage(string query, string value) => Evaluate(query, value).Coverage;

    internal static bool ContainsNormalizedTokenOrPhrase(string query, string value)
    {
        var queryTokens = SplitTokens(query).ToArray();
        var valueTokens = SplitTokens(value).ToArray();
        if (valueTokens.Length == 0 || valueTokens.Length > queryTokens.Length)
        {
            return false;
        }

        for (var start = 0; start <= queryTokens.Length - valueTokens.Length; start++)
        {
            if (queryTokens.AsSpan(start, valueTokens.Length).SequenceEqual(valueTokens))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<WritingScript> CountedScripts(IReadOnlyCollection<string> tokens)
    {
        var scripts = new HashSet<WritingScript>();
        foreach (var token in tokens)
        {
            scripts.Add(WritingScriptClassifier.Classify(token));
        }

        return scripts;
    }

    private static HashSet<string> Tokenize(string value)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in SplitTokens(value))
        {
            if (token.Length >= 3 && !StopWords.Contains(token))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static IEnumerable<string> SplitTokens(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = char.ToLowerInvariant(character);
                continue;
            }

            if (length > 0)
            {
                yield return new string(buffer, 0, length);
                length = 0;
            }
        }

        if (length > 0)
        {
            yield return new string(buffer, 0, length);
        }
    }
}
