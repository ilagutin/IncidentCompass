namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// One hunk: where in the base it starts, how many lines each side spans, and the body.
/// </summary>
/// <param name="OldStart">The header's base start, one-based, or zero when <paramref name="OldCount"/> is zero.</param>
/// <param name="OldCount">Base lines the hunk consumes.</param>
/// <param name="NewStart">The header's result start, one-based, or zero when <paramref name="NewCount"/> is zero.</param>
/// <param name="NewCount">Result lines the hunk produces.</param>
/// <param name="Lines">The body, in order.</param>
/// <param name="OldEndsWithoutNewline">
/// A <c>\ No newline at end of file</c> marker followed a base-side line, so the base file must end
/// without a terminator for this hunk to match.
/// </param>
/// <param name="NewEndsWithoutNewline">
/// The same marker followed a result-side line, so the result must be written without a final
/// terminator.
/// </param>
/// <remarks>
/// The two markers are carried rather than dropped because the tree identity is over exact bytes. A
/// patch that silently added or removed a final newline would produce a result whose identity no
/// reviewer could predict from the diff they approved.
/// </remarks>
internal sealed record SourcePatchHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<SourcePatchLine> Lines,
    bool OldEndsWithoutNewline,
    bool NewEndsWithoutNewline)
{
    /// <summary>
    /// The zero-based base index the hunk starts at. A hunk that consumes no base line inserts after
    /// the line its header names, so its index is that number; every other hunk matches from the
    /// line its header names, so its index is one less.
    /// </summary>
    public int OldIndex => OldCount == 0 ? OldStart : OldStart - 1;

    /// <summary>The same convention on the result side, used to check the header's own arithmetic.</summary>
    public int NewIndex => NewCount == 0 ? NewStart : NewStart - 1;
}
