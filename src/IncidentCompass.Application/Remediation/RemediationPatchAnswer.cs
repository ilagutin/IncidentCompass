namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Recovers the unified diff from a model answer, or decides there isn't one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all, given that the parser would refuse prose anyway.</b> Because a fenced
/// answer is the common correct answer and the parser cannot read one. Providers wrap code in a
/// <c>```</c> block whatever the instruction says, and the fence lines are not diff syntax, so
/// without this step the ordinary well-meant answer is refused as malformed and the model is
/// reprompted for a mistake it did not make. Unwrapping is the job; refusing everything else is the
/// consequence of doing it exactly.
/// </para>
/// <para>
/// <b>What is accepted.</b> Either the answer is a diff, or it is exactly one fenced block whose
/// content is a diff and which has nothing but blank lines around it. An info string is allowed only
/// when it is <c>diff</c> or <c>patch</c>. The first line of the recovered text must open a diff
/// section, <c>diff --git </c> or <c>--- </c>. Everything else is not a patch: a sentence before the
/// fence, a sentence after it, two blocks, a block of something else, an apology, an empty answer.
/// The instruction says a diff and nothing else, and this is what enforces it.
/// </para>
/// <para>
/// <b>What is deliberately not repaired.</b> Line endings. A carriage return inside the recovered
/// text stays exactly where it was, because a body line carries a file's exact bytes and the parser
/// and the tree identity both treat a carriage return as part of the base. Normalizing here would
/// let a diff written for one checkout match the other, which is the one thing the byte-exact
/// context check exists to prevent. Nothing else is repaired either: no whitespace is trimmed inside
/// the block, no header is inferred and no hunk count is corrected. The recovered text is the text
/// that is parsed, applied and recorded, and a reviewer reads the same bytes.
/// </para>
/// </remarks>
internal static class RemediationPatchAnswer
{
    private const string Fence = "```";

    private static readonly string[] AllowedInfoStrings = ["diff", "patch"];

    /// <summary>
    /// Returns the diff the answer carries, or <see langword="null" /> when it carries none.
    /// </summary>
    public static string? Extract(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var lines = content.Split('\n');
        if (!TryTrimBlank(lines, 0, lines.Length, out var start, out var end))
        {
            return null;
        }

        if (IsFence(lines[start]) && !TryUnwrap(lines, ref start, ref end))
        {
            return null;
        }

        if (start >= end || !OpensADiffSection(lines[start]))
        {
            return null;
        }

        // Joined on line feeds and terminated, which is the shape the parser splits on. Any
        // carriage return a line carries is left inside it.
        return string.Join('\n', lines[start..end]) + '\n';
    }

    /// <summary>
    /// Narrows a fenced answer to the block's content, or fails when the answer is not exactly one
    /// well-formed block and nothing else.
    /// </summary>
    private static bool TryUnwrap(string[] lines, ref int start, ref int end)
    {
        if (!IsAllowedInfoString(lines[start]))
        {
            return false;
        }

        // The closing fence must be the answer's last non-blank line, and must be a bare fence.
        // Anything after it is commentary, and an answer with commentary is not a diff and nothing
        // else. A diff line never begins with a backtick, so no diff can close its own block.
        var close = end - 1;
        if (close <= start || !string.Equals(lines[close].Trim(), Fence, StringComparison.Ordinal))
        {
            return false;
        }

        return TryTrimBlank(lines, start + 1, close, out start, out end);
    }

    /// <summary>
    /// Narrows a range to its first and last non-blank lines, and says whether any remain.
    /// </summary>
    private static bool TryTrimBlank(string[] lines, int from, int to, out int start, out int end)
    {
        start = from;
        end = to;
        while (start < end && string.IsNullOrWhiteSpace(lines[start]))
        {
            start++;
        }

        while (end > start && string.IsNullOrWhiteSpace(lines[end - 1]))
        {
            end--;
        }

        return start < end;
    }

    private static bool IsFence(string line) =>
        line.Trim().StartsWith(Fence, StringComparison.Ordinal);

    private static bool IsAllowedInfoString(string line)
    {
        var info = line.Trim()[Fence.Length..].Trim();
        return info.Length == 0 ||
            AllowedInfoStrings.Contains(info, StringComparer.OrdinalIgnoreCase);
    }

    private static bool OpensADiffSection(string line) =>
        line.StartsWith("diff --git ", StringComparison.Ordinal) ||
        line.StartsWith("--- ", StringComparison.Ordinal);
}
