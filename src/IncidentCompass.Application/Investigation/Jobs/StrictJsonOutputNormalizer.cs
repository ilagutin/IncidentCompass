namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Removes the one Markdown wrapper tolerated at the model boundary. The normalizer deliberately
/// does not repair JSON or extract a fenced block from surrounding prose.
/// </summary>
internal static class StrictJsonOutputNormalizer
{
    public static bool TryNormalize(string content, out string normalizedJson)
    {
        normalizedJson = content.Trim();
        if (!StartsWithFence(normalizedJson))
        {
            return true;
        }

        var firstLineEnd = normalizedJson.IndexOf('\n');
        var lastLineStart = normalizedJson.LastIndexOf('\n');
        if (firstLineEnd < 0 || lastLineStart <= firstLineEnd)
        {
            normalizedJson = string.Empty;
            return false;
        }

        var openingLine = normalizedJson[..firstLineEnd].TrimEnd('\r').Trim();
        if (!TryReadOpeningFence(openingLine, out var marker))
        {
            normalizedJson = string.Empty;
            return false;
        }

        var closingLine = normalizedJson[(lastLineStart + 1)..].Trim();
        if (!string.Equals(closingLine, marker, StringComparison.Ordinal))
        {
            normalizedJson = string.Empty;
            return false;
        }

        var body = normalizedJson[(firstLineEnd + 1)..lastLineStart].Trim();
        if (body.Length == 0 || ContainsFenceLine(body))
        {
            normalizedJson = string.Empty;
            return false;
        }

        normalizedJson = body;
        return true;
    }

    private static bool StartsWithFence(string content) =>
        content.StartsWith("```", StringComparison.Ordinal) ||
        content.StartsWith("~~~", StringComparison.Ordinal);

    private static bool TryReadOpeningFence(string line, out string marker)
    {
        marker = line.StartsWith("```", StringComparison.Ordinal)
            ? "```"
            : line.StartsWith("~~~", StringComparison.Ordinal)
                ? "~~~"
                : string.Empty;
        if (marker.Length == 0)
        {
            return false;
        }

        var language = line[marker.Length..].Trim();
        return language.Length == 0 || string.Equals(language, "json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsFenceLine(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            var candidate = line.TrimStart().TrimEnd('\r');
            if (StartsWithFence(candidate))
            {
                return true;
            }
        }

        return false;
    }
}
