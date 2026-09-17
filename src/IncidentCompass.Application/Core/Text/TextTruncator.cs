namespace IncidentCompass.Application.Core.Text;

/// <summary>
/// The one text cut in this solution. It bounds a value by UTF-16 code units and never leaves half
/// of a surrogate pair behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the pair matters.</b> A cut that fell between a high and a low surrogate left a lone
/// surrogate at the end of the result, which is not a character and not valid text. Two places
/// downstream then had to answer for it: the model-facing encoder writes a lone surrogate as U+FFFD,
/// so the cut value reached a prompt with a replacement character the original did not have, and a
/// PostgreSQL <c>text</c> column cannot store one at all, so a truncated diagnostic or rationale
/// could fail its own insert. Dropping the whole pair costs one code point of a value that was
/// already being shortened.
/// </para>
/// <para>
/// <b>The bound still holds.</b> The result is at most <c>maxLength</c> code units including the
/// suffix, because moving the cut one unit earlier only ever makes it shorter. Text with no pair
/// straddling the cut is cut exactly where it was before.
/// </para>
/// </remarks>
internal static class TextTruncator
{
    public static string Truncate(string value, int maxLength, string suffix = "")
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        if (suffix.Length >= maxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(suffix), "Suffix length must be smaller than maxLength.");
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = CutOnRuneBoundary(value, maxLength - suffix.Length);
        return suffix.Length == 0
            ? value[..cut]
            : value[..cut] + suffix;
    }

    /// <summary>
    /// The cut, moved one code unit earlier when it would fall between a high and a low surrogate.
    /// Both indexes exist: a suffix shorter than <c>maxLength</c> keeps the cut at or above one, and
    /// the caller has already established that the value is longer than <c>maxLength</c>.
    /// </summary>
    private static int CutOnRuneBoundary(string value, int cut) =>
        char.IsHighSurrogate(value[cut - 1]) && char.IsLowSurrogate(value[cut]) ? cut - 1 : cut;
}
