using System.Globalization;
using System.Text;

namespace IncidentCompass.Application.Governance.Tools;

/// <summary>
/// The only way a worker tool can express which external thing a durable artifact points at. It is
/// built from a kind and one or more segments and renders as <c>kind:segment[:segment...]</c>, which
/// is the shape <c>triage_artifacts.domain_ref</c> already holds.
/// <para>
/// It exists because a domain reference is assembled out of connector text - a repository-relative
/// path, a release name, a ticket scope, an external id - and that text arrives from the same places
/// the payload beside it arrives from. The payload meets
/// <see cref="RedactedToolArtifactFactory"/> on its way to durable state; a raw <c>string</c>
/// property beside it met nothing, so a tool could put an arbitrary connector value into a stored
/// column simply by naming it. Requiring this type makes the shape of that column a compile-time
/// property of the tool contract rather than a convention each tool is trusted to follow.
/// </para>
/// <para>
/// Be precise about what the restriction buys. A value here is a single line of visible text: its
/// character set excludes every control and format character, every unassigned and private-use code
/// point, every ill-formed UTF-16 sequence and every whitespace character except the plain space.
/// Each segment is capped at <see cref="MaximumSegmentLength"/> characters and the whole reference at
/// <see cref="MaximumLength"/>. That is enough to say a domain reference cannot carry a document, a
/// source excerpt, a stack trace or a pasted multi-line secret block, because none of those survives
/// the line and length bounds. It is <em>not</em> a proof that no secret-shaped token can ever appear
/// in a reference: an API key is a short single-line run of printable characters, and a repository
/// really can hold a file whose name is one. That remaining case is why the factory redacts this
/// value with the same rules it redacts the payload with, rather than treating the bounded shape as
/// sufficient on its own. The two halves cover different things and neither replaces the other.
/// </para>
/// <para>
/// A single space is admitted deliberately, and so is every ordinary printable character an operating
/// system lets a filename hold, an emoji included. Real checkouts contain paths with spaces and
/// non-Latin names in them, and a rule that refused them would drop a legitimate
/// <c>source_lookup</c> hit for a reason that has nothing to do with safety. What is refused instead
/// is the invisible: a tab, a newline, a carriage return and the other non-space whitespace make one
/// line read as several, and the format characters - zero-width space, the bidirectional overrides,
/// the word joiner, the byte-order mark and the Unicode tag block - hide what follows them from
/// whoever reads the stored value. Unassigned and private-use code points are refused for the same
/// reason: nothing can say how a reader will render them.
/// </para>
/// <para>
/// U+FFFD REPLACEMENT CHARACTER is refused as well, and it is worth naming here because its category
/// is <see cref="UnicodeCategory.OtherSymbol"/> and a reader will not find it in the list above.
/// <c>EnumerateRunes</c> substitutes it for an ill-formed UTF-16 sequence rather than surfacing the
/// unpaired surrogate itself, so refusing the replacement character <em>is</em> how the ill-formed
/// case is caught. It refuses a genuine U+FFFD in a real filename along with it. That cost is
/// accepted rather than overlooked: it is the one printable character nothing can render honestly,
/// and refusing it costs that single match a <c>source_reference_rejected</c> limitation and nothing
/// else.
/// </para>
/// <para>
/// Both caps are measured in UTF-16 code units, which is what
/// <see cref="string.Length"/> counts and therefore what the caps actually bound. An astral code
/// point costs two of them. The column itself is untyped text, so the bound has no second opinion to
/// agree with; counting code units is simply counting the same thing the runtime counts.
/// </para>
/// </summary>
/// <remarks>
/// This is a class with an explicit <see cref="ToString"/> rather than a record, for the same reason
/// <see cref="ToolArtifactDraft"/> is: a record's synthesized <c>ToString</c> prints every member
/// wrapped in the type name, and this value is formatted into exception messages, tool output and
/// log arguments. Printing the value alone keeps those surfaces identical to what they were when the
/// property was a bare string.
/// </remarks>
public sealed class ArtifactDomainRef
{
    /// <summary>The separator between the kind and each segment, and therefore never inside one.</summary>
    public const char Separator = ':';

    /// <summary>
    /// The most UTF-16 code units one segment may carry. A repository-relative path is the longest
    /// segment this product produces and nothing bounds its length before it arrives here, so this is
    /// the only cap a deep checkout meets. A path past it cannot be expressed, which is why
    /// <see cref="TryCreate"/> exists: the caller that holds such a path drops the one match rather
    /// than failing the investigation.
    /// </summary>
    public const int MaximumSegmentLength = 200;

    /// <summary>
    /// The most UTF-16 code units the rendered reference may carry, separators and kind included. The
    /// column it is written to is a text column with no length of its own, so this is where the
    /// bound lives.
    /// </summary>
    public const int MaximumLength = 512;

    private ArtifactDomainRef(string value) => Value = value;

    /// <summary>The rendered reference, exactly as it is stored.</summary>
    public string Value { get; }

    /// <summary>
    /// Builds a reference from a kind and its segments, in the order they appear, and throws when it
    /// cannot. This is the form for a caller whose segments cannot break the rules - a literal
    /// provider name, a parsed integer, a <see cref="Guid"/> - where a refusal would be a bug in this
    /// file rather than a value a connector chose. A caller holding connector text of unbounded shape
    /// wants <see cref="TryCreate"/> instead.
    /// </summary>
    /// <param name="kind">
    /// The reference family, such as <c>source</c>, <c>ticket</c>, <c>worker</c> or
    /// <c>memory_item</c>. It must be lower-case snake case starting with a letter, which keeps the
    /// set of families readable at a glance and keeps a reader from having to ask whether two
    /// spellings of one family are the same family.
    /// </param>
    /// <param name="segments">
    /// At least one segment. Each is validated on its own, so a caller learns which of them was
    /// rejected without the message having to quote any of them.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The kind or a segment breaks one of the rules above. The message names the rule and the
    /// segment's position and never echoes the offending text: the text is exactly the untrusted
    /// connector value this type exists to bound, and an exception message reaches logs, which is
    /// the surface <c>docs/security-model.md</c> keeps connector text out of.
    /// </exception>
    public static ArtifactDomainRef Create(string kind, params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(segments);
        var failure = DescribeFailure(kind, segments, out var rendered);
        if (failure is { } refusal)
        {
            throw new ArgumentException(refusal.Message, refusal.Parameter);
        }

        return new ArtifactDomainRef(rendered!);
    }

    /// <summary>
    /// The non-throwing form: returns <see langword="null" /> when the kind or a segment breaks a
    /// rule, instead of raising.
    /// <para>
    /// It exists because an unrepresentable reference is a property of connector text, not a fault in
    /// the run. A repository-relative path longer than <see cref="MaximumSegmentLength"/> is
    /// something a deep monorepo produces on its own, and a throw would leave the worker tool that
    /// found it with no way to continue: the exception escapes the tool, the executor, the role
    /// runner and the delegate path alike, none of which classify it, so the attempt fails, retries
    /// deterministically and dead-letters the job with the ledger showing a tool call proposed and
    /// allowed and no outcome. Returning nothing lets the caller degrade in whatever way is honest
    /// for its own shape and record that it did.
    /// </para>
    /// <para>
    /// It returns the value rather than taking an <c>out</c> parameter because <c>segments</c> is a
    /// <c>params</c> array and has to come last, which would put the output ahead of the input it
    /// describes.
    /// </para>
    /// </summary>
    public static ArtifactDomainRef? TryCreate(string kind, params string[] segments)
    {
        if (kind is null || segments is null)
        {
            return null;
        }

        return DescribeFailure(kind, segments, out var rendered) is null
            ? new ArtifactDomainRef(rendered!)
            : null;
    }

    /// <summary>
    /// Whether one segment could appear in a reference. It is the same rule
    /// <see cref="Create"/> and <see cref="TryCreate"/> enforce, exposed so that configuration
    /// validation can refuse a release name or a role key at load rather than turning every later
    /// lookup that quotes it into a refusal. Duplicating the rule at the load boundary would be a
    /// second thing to keep true.
    /// </summary>
    public static bool IsValidSegment(string segment) => DescribeSegmentFailure(segment, 0) is null;

    /// <summary>Returns the rendered reference and nothing else. See the remarks on this type.</summary>
    public override string ToString() => Value;

    private static (string Message, string Parameter)? DescribeFailure(
        string kind,
        string[] segments,
        out string? rendered)
    {
        rendered = null;
        if (DescribeKindFailure(kind) is { } kindFailure)
        {
            return (kindFailure, nameof(kind));
        }

        if (segments.Length == 0)
        {
            return ("An artifact domain reference needs at least one segment.", nameof(segments));
        }

        var builder = new StringBuilder(kind);
        for (var index = 0; index < segments.Length; index++)
        {
            if (DescribeSegmentFailure(segments[index], index) is { } segmentFailure)
            {
                return (segmentFailure, nameof(segments));
            }

            builder.Append(Separator).Append(segments[index]);
        }

        if (builder.Length > MaximumLength)
        {
            return (
                $"An artifact domain reference must be at most {MaximumLength} characters.",
                nameof(segments));
        }

        rendered = builder.ToString();
        return null;
    }

    private static string? DescribeKindFailure(string kind)
    {
        var isSnakeCase = kind.Length > 0 &&
            kind[0] is >= 'a' and <= 'z' &&
            kind.All(character => character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');
        return isSnakeCase
            ? null
            : "An artifact domain reference kind must start with a lower-case letter and hold only" +
                " lower-case letters, digits and underscores.";
    }

    private static string? DescribeSegmentFailure(string segment, int index)
    {
        if (string.IsNullOrEmpty(segment))
        {
            return $"Artifact domain reference segment {index} must not be empty.";
        }

        if (segment.Length > MaximumSegmentLength)
        {
            return $"Artifact domain reference segment {index} must be at most" +
                $" {MaximumSegmentLength} characters.";
        }

        // Runes, not chars. A `foreach (var character in segment)` walks UTF-16 code units, and half
        // a surrogate pair is neither a control character nor whitespace, so every astral code point
        // - the Unicode tag block among them - walked straight through a per-char rule.
        foreach (var rune in segment.EnumerateRunes())
        {
            if (rune.Value == Separator)
            {
                return $"Artifact domain reference segment {index} must not hold the separator" +
                    $" '{Separator}'.";
            }

            // Control is what keeps a reference to one line; Format is what keeps it from hiding
            // what follows it, and it is the half a control-character rule misses entirely. The
            // replacement character stands in for the surrogate case: EnumerateRunes substitutes
            // U+FFFD for an ill-formed UTF-16 sequence rather than surfacing the surrogate itself.
            if (rune == Rune.ReplacementChar || IsRefusedCategory(rune))
            {
                return $"Artifact domain reference segment {index} must not hold a control," +
                    " format, unassigned or private-use character.";
            }

            if (Rune.IsWhiteSpace(rune) && rune.Value != ' ')
            {
                return $"Artifact domain reference segment {index} must not hold whitespace other" +
                    " than the space character.";
            }
        }

        return null;
    }

    // Printable symbol categories are deliberately absent. A real filename can hold an emoji, and
    // refusing one would be the same mistake as refusing a space: a legitimate hit lost to a rule
    // that protects nothing. U+FFFD is the one printable symbol that is refused, and it is refused
    // by name at the call site rather than by widening this list, so that no whole category of
    // renderable characters is caught along with it.
    //
    // Surrogate cannot be reached: EnumerateRunes yields U+FFFD for an ill-formed sequence and never
    // a surrogate rune. It is written down anyway. This is a security rule, and an arm that costs
    // nothing to keep is worth more than the tidiness of removing it - if a future caller decodes
    // runes some other way, the category is already refused rather than newly permitted.
    private static bool IsRefusedCategory(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.Control or
            UnicodeCategory.Format or
            UnicodeCategory.Surrogate or
            UnicodeCategory.PrivateUse or
            UnicodeCategory.OtherNotAssigned;
}
