namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// How many lines of each side are still owed, and whether a side has been closed by a
/// no-newline marker. A closed side cannot take another line: the marker says that side's file
/// ended there, so anything after it on that side contradicts the marker.
/// </summary>
internal sealed class SourcePatchHunkBodyState(int remainingOld, int remainingNew)
{
    private char lastOrigin;

    public bool OldEndsWithoutNewline { get; private set; }

    public bool NewEndsWithoutNewline { get; private set; }

    public bool HasRemaining => remainingOld > 0 || remainingNew > 0;

    public string? Take(char origin)
    {
        var touchesOld = origin is ' ' or '-';
        var touchesNew = origin is ' ' or '+';
        if ((touchesOld && OldEndsWithoutNewline) || (touchesNew && NewEndsWithoutNewline))
        {
            return SourcePatchCodes.Malformed;
        }

        if ((touchesOld && remainingOld == 0) || (touchesNew && remainingNew == 0))
        {
            return SourcePatchCodes.HunkCountMismatch;
        }

        remainingOld -= touchesOld ? 1 : 0;
        remainingNew -= touchesNew ? 1 : 0;
        lastOrigin = origin;
        return null;
    }

    public string? MarkNoNewline(string line)
    {
        if (!string.Equals(line, SourcePatchText.NoNewlineMarker, StringComparison.Ordinal) || lastOrigin == '\0')
        {
            return SourcePatchCodes.Malformed;
        }

        OldEndsWithoutNewline |= lastOrigin is ' ' or '-';
        NewEndsWithoutNewline |= lastOrigin is ' ' or '+';
        return null;
    }
}
