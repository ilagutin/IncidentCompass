using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace IncidentCompass.Application.Core.Serialization;

/// <summary>
/// The single JSON encoding used for text a model reads: prompt values, tool results and delegate
/// results. It is not the encoding of anything durable, hashed or sent to a provider as a request
/// body; those keep <see cref="CanonicalJsonSerializer"/> and the framework default.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The framework's default encoder allows only Basic Latin, so every other
/// character leaves as a six-character <c>\uXXXX</c> escape. An incident written in Russian or Polish
/// therefore reached the model as escapes rather than as words: a strong model decodes them, a small
/// local one - the runtime this product is built for - may not, the prompt is several times longer
/// than its text, and the character-based token estimate charges the context window and the attempt's
/// token budget for that inflation. This encoder lets a letter be a letter.
/// </para>
/// <para>
/// <b>What stays escaped, and why that matters.</b> The quoting itself is unchanged: every untrusted
/// text value is still written as a JSON string literal, which is what delimits incident text inside
/// the prompt's <c>BEGIN_UNTRUSTED_INCIDENT_CONTEXT</c> boundary. The characters that could end a
/// quoted line or change what a reader sees are all still escaped:
/// </para>
/// <list type="bullet">
/// <item><description><c>"</c> and <c>\</c>, so a value cannot close its own literal.</description></item>
/// <item><description>Every character of category Cc, so a newline or a carriage return can never
/// break one value across two prompt lines.</description></item>
/// <item><description>The characters the framework's JavaScript encoder escapes regardless of the
/// allow list: <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c>, <c>"</c>, <c>+</c> and <c>`</c>.</description></item>
/// <item><description>Categories Zl and Zp, the line and paragraph separators U+2028 and
/// U+2029.</description></item>
/// <item><description>Every space separator (category Zs) other than the ordinary space U+0020, so a
/// non-breaking space and an ideographic space are visible for what they are.</description></item>
/// <item><description>Every format character (category Cf) of the Basic Multilingual Plane, forbidden
/// here explicitly: the bidirectional controls U+202A-U+202E and U+2066-U+2069, the zero-width
/// characters U+200B-U+200F, U+2060-U+2064, U+FEFF, the soft hyphen U+00AD, the Arabic letter mark and
/// the rest. An invisible or direction-changing character inside attacker-influenced text is exactly
/// what should not reach a model looking like nothing at all. The zero-width joiner and non-joiner are
/// in this category too, so a script that spells a word with one carries an escape inside otherwise
/// readable text.</description></item>
/// <item><description>Categories Cn and Co, the unassigned and private-use code points, which the
/// framework encoder refuses for us.</description></item>
/// <item><description>Every supplementary-plane character. The framework encoder escapes those as
/// surrogate pairs, so an emoji or a rare CJK ideograph stays as two escapes. That is
/// accepted: the scripts an incident is written in live in the BMP.</description></item>
/// </list>
/// <para>
/// <b>What this is not.</b> It is prompt hygiene, not enforcement. The LLM is not a security
/// boundary; the quoting is a delimiter and nothing more. Letters that look like other letters read
/// as themselves here exactly as they would in any UTF-8 prompt, and so does a character that is
/// invisible without being a format character - a Hangul filler, a variation selector, the Braille
/// blank, an unbounded stack of combining marks. None of those can end a value or a line, and how a
/// human is shown incident text is the obligation of a surface that shows it, which this backend does
/// not have. <c>docs/security-model.md</c>, "Untrusted prompt boundary", states both residuals.
/// </para>
/// <para>
/// <b>ASCII is byte-identical to before.</b> The allowed set differs from the framework default only
/// outside Basic Latin, so an English incident produces exactly the bytes it produced before this
/// encoder existed, and every fixture and pinned prompt built from one stays valid.
/// </para>
/// </remarks>
internal static class ModelFacingJson
{
    /// <summary>
    /// The encoder for model-facing text: the whole Basic Multilingual Plane, minus the format
    /// characters, minus whatever the framework encoder forbids on its own.
    /// </summary>
    internal static JavaScriptEncoder Encoder { get; } = CreateEncoder();

    /// <summary>
    /// Framework defaults with <see cref="Encoder"/> in place. Nothing else is configured: no naming
    /// policy, no indentation and no converter, so a payload's property names and number formatting
    /// are the ones the call site already produced.
    /// </summary>
    private static JsonSerializerOptions Options { get; } = new() { Encoder = Encoder };

    /// <summary>Writes one value as a JSON string literal, quotes included.</summary>
    public static string SerializeString(string? value) =>
        JsonSerializer.Serialize(value ?? string.Empty, Options);

    /// <summary>Writes one object as a JSON document.</summary>
    public static string Serialize<TValue>(TValue value) =>
        JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Rewrites a JSON document that is about to be shown to a model so that every string literal in
    /// it obeys the same rule as <see cref="SerializeString"/>.
    /// </summary>
    public static string Normalize(JsonElement element) => NormalizeRawJson(element.GetRawText());

    /// <summary>
    /// Rewrites the string literals of a JSON document so that each one obeys the same rule as
    /// <see cref="SerializeString"/>, whatever mix of raw characters and <c>\uXXXX</c> escapes came
    /// in, and copies everything outside a literal byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The postcondition.</b> In every string literal of the result, a character
    /// <see cref="Encoder"/> allows appears as itself and a character it would escape appears as a
    /// <c>\uXXXX</c> escape. That holds in both directions: an escape naming an allowed character is
    /// decoded, and a raw non-ASCII character the encoder would escape - a format character, a
    /// bidirectional control, U+2028, a non-breaking space, a private-use or unassigned code point -
    /// is escaped. So the rule does not depend on which caller passed the text or on how that text
    /// was produced, which matters because the two provenances differ: an element built in this
    /// process carries an escape for every non-ASCII character, while a payload read back from a
    /// PostgreSQL <c>jsonb</c> column carries the characters themselves.
    /// </para>
    /// <para>
    /// <b>Values never change.</b> Escaping and unescaping are the only edits, so parsing the output
    /// yields exactly what parsing the input would. Two consequences are worth naming. A raw
    /// supplementary-plane character is written as the two escapes <see cref="SerializeString"/>
    /// writes for it. An unpaired surrogate is copied through rather than replaced with U+FFFD as
    /// the serializer would: no valid JSON text and no string decoded from UTF-8 can hold one, and
    /// replacing it would make this the one place that alters a value.
    /// </para>
    /// <para>
    /// <b>Why a rewrite rather than a re-serialize.</b> Re-serializing would normalize the escapes
    /// and also rewrite the document's own whitespace, which would change every prompt built from a
    /// payload that was not already compact. Rewriting in place touches nothing else, so an ASCII
    /// document comes back as the same instance.
    /// </para>
    /// </remarks>
    public static string NormalizeRawJson(string rawJson)
    {
        ArgumentNullException.ThrowIfNull(rawJson);

        // An all-ASCII document with no backslash has nothing to decode and nothing to escape. It is
        // returned unchanged rather than rebuilt, which is the common case for an English incident.
        if (Ascii.IsValid(rawJson) && !rawJson.Contains('\\', StringComparison.Ordinal))
        {
            return rawJson;
        }

        var builder = new StringBuilder(rawJson.Length);
        var insideString = false;
        var index = 0;
        while (index < rawJson.Length)
        {
            var current = rawJson[index];
            if (!insideString)
            {
                builder.Append(current);
                insideString = current == '"';
                index++;
                continue;
            }

            if (current == '"')
            {
                builder.Append(current);
                insideString = false;
                index++;
                continue;
            }

            if (current == '\\')
            {
                index += AppendEscape(builder, rawJson, index);
                continue;
            }

            // Only non-ASCII characters are reconsidered. An ASCII character inside a literal is
            // already either legal raw or an escape the branch above handled, so leaving it alone is
            // what keeps an ASCII document byte-identical to the text this was given.
            if (Ascii.IsValid(current))
            {
                builder.Append(current);
                index++;
                continue;
            }

            index += AppendRawCharacter(builder, rawJson, index);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends one raw non-ASCII character, escaped when <see cref="Encoder"/> would escape it, and
    /// returns how many characters of <paramref name="rawJson"/> it consumed.
    /// </summary>
    private static int AppendRawCharacter(StringBuilder builder, string rawJson, int index)
    {
        var current = rawJson[index];
        if (char.IsHighSurrogate(current) &&
            index + 1 < rawJson.Length &&
            char.IsLowSurrogate(rawJson[index + 1]))
        {
            AppendEscapeOf(builder, current);
            AppendEscapeOf(builder, rawJson[index + 1]);
            return 2;
        }

        // A surrogate is asked about first: WillEncode throws on a surrogate code point rather than
        // answering, because a surrogate is not a scalar value.
        if (char.IsSurrogate(current) || !Encoder.WillEncode(current))
        {
            builder.Append(current);
            return 1;
        }

        AppendEscapeOf(builder, current);
        return 1;
    }

    private static void AppendEscapeOf(StringBuilder builder, char value) =>
        builder.Append("\\u").Append(((int)value).ToString("X4", CultureInfo.InvariantCulture));

    /// <summary>
    /// Appends one escape sequence starting at <paramref name="index"/> and returns how many
    /// characters of <paramref name="rawJson"/> it consumed. A two-character escape such as
    /// <c>\\</c> is copied whole, which is what keeps a literal backslash from being read as the
    /// start of a <c>\uXXXX</c> sequence and decoded twice.
    /// </summary>
    private static int AppendEscape(StringBuilder builder, string rawJson, int index)
    {
        if (index + 5 >= rawJson.Length || rawJson[index + 1] != 'u')
        {
            var length = Math.Min(2, rawJson.Length - index);
            builder.Append(rawJson, index, length);
            return length;
        }

        if (!ushort.TryParse(
                rawJson.AsSpan(index + 2, 4),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var codeUnit))
        {
            builder.Append(rawJson, index, 2);
            return 2;
        }

        var decoded = (char)codeUnit;
        if (char.IsSurrogate(decoded) || Encoder.WillEncode(decoded))
        {
            builder.Append(rawJson, index, 6);
        }
        else
        {
            builder.Append(decoded);
        }

        return 6;
    }

    /// <summary>
    /// The format characters are computed from <see cref="CharUnicodeInfo.GetUnicodeCategory(char)"/>
    /// rather than listed, so the set is the runtime's own answer for its Unicode version instead of
    /// a table that goes stale.
    /// </summary>
    private static JavaScriptEncoder CreateEncoder()
    {
        var settings = new TextEncoderSettings();
        settings.AllowRange(UnicodeRanges.All);
        settings.ForbidCharacters(CollectFormatCharacters());
        return JavaScriptEncoder.Create(settings);
    }

    private static char[] CollectFormatCharacters()
    {
        var formatCharacters = new List<char>();
        for (var codePoint = 0; codePoint <= char.MaxValue; codePoint++)
        {
            var candidate = (char)codePoint;
            if (CharUnicodeInfo.GetUnicodeCategory(candidate) == UnicodeCategory.Format)
            {
                formatCharacters.Add(candidate);
            }
        }

        return [.. formatCharacters];
    }
}
