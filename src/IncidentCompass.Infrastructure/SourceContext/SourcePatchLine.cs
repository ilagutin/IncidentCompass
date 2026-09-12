namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// One line of a hunk body: the origin character that says which side it belongs to, and the exact
/// text after it.
/// </summary>
/// <param name="Origin">
/// <c>' '</c> for a line both sides share, <c>'-'</c> for a line only the base has, <c>'+'</c> for a
/// line only the result has. Nothing else parses.
/// </param>
/// <param name="Text">
/// The line's bytes without a terminator, and without any normalization. A carriage return before
/// the line feed stays part of the text, so a patch written with one line ending does not match a
/// file written with the other. That is deliberate: a diff applies to bytes, the tree identity
/// treats the two as different bases, and silently repairing the difference here would let a patch
/// claim to apply to a base it was never generated from.
/// </param>
internal sealed record SourcePatchLine(char Origin, string Text);
