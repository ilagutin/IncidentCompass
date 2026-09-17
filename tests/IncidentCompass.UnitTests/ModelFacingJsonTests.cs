using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.Serialization;
using static IncidentCompass.UnitTests.ModelFacingJsonText;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The encoding rule for text a model reads: a letter of any Basic Multilingual Plane script is
/// itself, and everything that could end a quoted line, hide itself or move what follows it stays
/// escaped.
/// </summary>
public sealed class ModelFacingJsonTests
{
    [Theory]
    [InlineData("Тайм-аут запроса к платежам")]
    [InlineData("Zażółć gęślą jaźń")]
    [InlineData("Σφάλμα υπερχρόνισης")]
    [InlineData("结账请求超时")]
    [InlineData("مهلة الدفع")]
    public void ALetterOfAnyBmpScript_ReachesTheModelAsItself(string value)
    {
        var serialized = ModelFacingJson.SerializeString(value);

        Assert.Equal(Quoted(value), serialized);
        Assert.DoesNotContain(Backslash, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCharactersThatCouldEndAQuotedLine_StayEscaped()
    {
        Assert.Equal(Quoted(Hex(0x0022)), ModelFacingJson.SerializeString("\""));
        Assert.Equal(Quoted(Backslash + Backslash), ModelFacingJson.SerializeString("\\"));
        Assert.Equal(Quoted(Backslash + "n"), ModelFacingJson.SerializeString("\n"));
        Assert.Equal(Quoted(Backslash + "r"), ModelFacingJson.SerializeString("\r"));
        Assert.Equal(Quoted(Backslash + "t"), ModelFacingJson.SerializeString("\t"));
    }

    [Theory]
    [InlineData(0x0000)]
    [InlineData(0x001F)]
    [InlineData(0x007F)]
    [InlineData(0x0085)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    public void EveryControlCharacterAndSeparator_StaysEscaped(int codePoint)
    {
        Assert.Equal(Quoted(Hex(codePoint)), ModelFacingJson.SerializeString(Ch(codePoint)));
    }

    /// <summary>
    /// An invisible or direction-changing character inside untrusted text is the one class this
    /// encoder forbids beyond what the framework already refuses.
    /// </summary>
    [Theory]
    [InlineData(0x00AD)]
    [InlineData(0x061C)]
    [InlineData(0x200B)]
    [InlineData(0x200D)]
    [InlineData(0x202E)]
    [InlineData(0x2066)]
    [InlineData(0xFEFF)]
    public void EveryFormatCharacterNamedByTheRule_StaysEscaped(int codePoint)
    {
        Assert.Equal(Quoted(Hex(codePoint)), ModelFacingJson.SerializeString(Ch(codePoint)));
    }

    [Fact]
    public void EveryFormatCharacterOfThePlane_StaysEscaped()
    {
        var unescaped = new List<int>();
        for (var codeUnit = 0; codeUnit <= char.MaxValue; codeUnit++)
        {
            var candidate = (char)codeUnit;
            if (CharUnicodeInfo.GetUnicodeCategory(candidate) == UnicodeCategory.Format &&
                !ModelFacingJson.Encoder.WillEncode(codeUnit))
            {
                unescaped.Add(codeUnit);
            }
        }

        Assert.Empty(unescaped);
    }

    /// <summary>
    /// The HTML-sensitive characters are the framework encoder's own business, not this rule's. The
    /// test pins what it does with them rather than asking for it.
    /// </summary>
    [Fact]
    public void TheHtmlSensitiveCharacters_KeepTheFrameworksOwnBehaviour()
    {
        Assert.Equal(Quoted(Hex(0x003C)), ModelFacingJson.SerializeString("<"));
        Assert.Equal(Quoted(Hex(0x003E)), ModelFacingJson.SerializeString(">"));
        Assert.Equal(Quoted(Hex(0x0026)), ModelFacingJson.SerializeString("&"));
        Assert.Equal(Quoted(Hex(0x0027)), ModelFacingJson.SerializeString("'"));
        Assert.Equal(Quoted(Hex(0x002B)), ModelFacingJson.SerializeString("+"));
        Assert.Equal(Quoted(Hex(0x0060)), ModelFacingJson.SerializeString("`"));
        Assert.Equal(Quoted("/"), ModelFacingJson.SerializeString("/"));
    }

    /// <summary>
    /// A supplementary-plane character stays as the two escapes the framework writes for it. Emoji
    /// and rare CJK are therefore still unreadable in a prompt, which is the accepted cost.
    /// </summary>
    [Fact]
    public void ASupplementaryPlaneCharacter_StaysEscapedAsASurrogatePair()
    {
        Assert.Equal(
            Quoted(Hex(0xD83D) + Hex(0xDE00)),
            ModelFacingJson.SerializeString(Ch(0x1F600)));
    }

    [Fact]
    public void AnUnassignedCodePointAndANonCharacter_StayEscaped()
    {
        Assert.Equal(Quoted(Hex(0x0378)), ModelFacingJson.SerializeString(Ch(0x0378)));
        Assert.Equal(Quoted(Hex(0xFFFE)), ModelFacingJson.SerializeString(Ch(0xFFFE)));
    }

    [Theory]
    [InlineData("plain ascii incident summary")]
    [InlineData("quote \" inside")]
    [InlineData("backslash \\ inside")]
    [InlineData("line\nbreak")]
    [InlineData("tab\tseparated <b> & 'quoted'")]
    [InlineData("")]
    public void AsciiText_IsByteIdenticalToTheFrameworkDefault(string value)
    {
        Assert.Equal(JsonSerializer.Serialize(value), ModelFacingJson.SerializeString(value));
    }

    [Fact]
    public void EveryAsciiCodePoint_IsByteIdenticalToTheFrameworkDefault()
    {
        var differing = new List<int>();
        for (var codePoint = 0; codePoint < 128; codePoint++)
        {
            var value = "a" + (char)codePoint + "b";
            if (!string.Equals(
                    JsonSerializer.Serialize(value),
                    ModelFacingJson.SerializeString(value),
                    StringComparison.Ordinal))
            {
                differing.Add(codePoint);
            }
        }

        Assert.Empty(differing);
    }

    [Fact]
    public void AnObject_IsSerializedUnderTheSameRule()
    {
        var serialized = ModelFacingJson.Serialize(new { status = "Failed", errorMessage = "Тайм-аут <x>" });

        Assert.Equal(
            "{\"status\":\"Failed\",\"errorMessage\":\"Тайм-аут " + Hex(0x003C) + "x" + Hex(0x003E) + "\"}",
            serialized);
    }

    [Fact]
    public void NormalizingRawJson_TurnsALetterEscapeIntoTheLetter()
    {
        var input = "{\"message\":\"" + Hex(0x0422) + Hex(0x0430) + Hex(0x0439) + Hex(0x043C) + "\"}";

        Assert.Equal("{\"message\":\"Тайм\"}", ModelFacingJson.NormalizeRawJson(input));
    }

    [Fact]
    public void NormalizingRawJson_KeepsAnEscapeTheEncoderWouldHaveWritten()
    {
        var input = "{\"m\":\"" + Hex(0x0022) + Hex(0x000A) + Hex(0x202E) + Hex(0xD83D) + Hex(0xDE00) + "\"}";

        Assert.Equal(input, ModelFacingJson.NormalizeRawJson(input));
    }

    /// <summary>
    /// An escaped backslash is two characters of content, not the start of an escape, so the
    /// <c>u0422</c> after it is data and stays.
    /// </summary>
    [Fact]
    public void NormalizingRawJson_DoesNotDecodeAnEscapedBackslash()
    {
        var input = "{\"m\":\"" + Backslash + Backslash + "u0422\"}";

        Assert.Equal(input, ModelFacingJson.NormalizeRawJson(input));
    }

    /// <summary>
    /// A key is a string literal too, and whitespace between tokens is not touched, which is what
    /// keeps a payload read back from a <c>jsonb</c> column rendering as it did before.
    /// </summary>
    [Fact]
    public void NormalizingRawJson_DecodesKeysAndPreservesLayout()
    {
        var input = "{\n  \"" + Hex(0x043A) + "\": [\n    \"" + Hex(0x0430) + "\"\n  ]\n}";

        Assert.Equal("{\n  \"к\": [\n    \"а\"\n  ]\n}", ModelFacingJson.NormalizeRawJson(input));
    }

    /// <summary>
    /// The postcondition runs in both directions: a raw character the encoder would escape is
    /// escaped, so a caller handing this the text of a payload read back from a <c>jsonb</c> column
    /// cannot put a format character or a separator into a tool message.
    /// </summary>
    [Theory]
    [InlineData(0x202E)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    [InlineData(0x200B)]
    [InlineData(0x00A0)]
    [InlineData(0x3000)]
    [InlineData(0xFEFF)]
    [InlineData(0x00AD)]
    [InlineData(0xE000)]
    [InlineData(0x1CBB)]
    public void NormalizingRawJson_EscapesARawCharacterTheEncoderWouldEscape(int codePoint)
    {
        var input = "{\"m\":\"a" + Ch(codePoint) + "b\"}";

        Assert.Equal("{\"m\":\"a" + Hex(codePoint) + "b\"}", ModelFacingJson.NormalizeRawJson(input));
    }

    [Fact]
    public void NormalizingRawJson_LeavesARawCharacterTheEncoderAllows()
    {
        const string input = "{\"m\":\"Тайм-аут zażółć 超时\"}";

        Assert.Equal(input, ModelFacingJson.NormalizeRawJson(input));
    }

    /// <summary>
    /// A raw supplementary-plane character becomes the two escapes the serializer writes for it, so
    /// a payload holding an emoji renders the same whichever provenance it came from.
    /// </summary>
    [Fact]
    public void NormalizingRawJson_EscapesARawSupplementaryPlaneCharacter()
    {
        var input = "{\"m\":\"" + Ch(0x1F600) + "\"}";

        Assert.Equal("{\"m\":\"" + Hex(0xD83D) + Hex(0xDE00) + "\"}", ModelFacingJson.NormalizeRawJson(input));
    }

    /// <summary>
    /// An unpaired surrogate is copied through rather than replaced with U+FFFD as the serializer
    /// would: no valid JSON text holds one, and replacing it would make this the one place that
    /// alters a value.
    /// </summary>
    [Fact]
    public void NormalizingRawJson_CopiesAnUnpairedRawSurrogateThrough()
    {
        var loneHigh = "{\"m\":\"" + ((char)0xD83D).ToString() + "\"}";
        var loneLow = "{\"m\":\"" + ((char)0xDE00).ToString() + "\"}";

        Assert.Equal(loneHigh, ModelFacingJson.NormalizeRawJson(loneHigh));
        Assert.Equal(loneLow, ModelFacingJson.NormalizeRawJson(loneLow));
    }

    [Theory]
    [InlineData("{\"m\":\"plain\"}")]
    [InlineData("{\n  \"m\": [\n    \"pretty printed\"\n  ]\n}")]
    [InlineData("{\"m\": \"jsonb spacing\", \"n\": 1}")]
    [InlineData("{\"score\":0.5,\"matched\":true,\"noMatchReason\":null}")]
    [InlineData("[1,2,3]")]
    public void NormalizingRawJson_ReturnsAnAsciiDocumentUnchanged(string input)
    {
        Assert.Same(input, ModelFacingJson.NormalizeRawJson(input));
    }

    [Theory]
    [InlineData("u0442")]
    [InlineData("u04ab")]
    [InlineData("u04Ab")]
    public void NormalizingRawJson_DecodesLowerAndMixedCaseHex(string escapeBody)
    {
        var input = "{\"m\":\"" + Backslash + escapeBody + "\"}";

        var normalized = ModelFacingJson.NormalizeRawJson(input);

        Assert.DoesNotContain(Backslash, normalized, StringComparison.Ordinal);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(input), JsonNode.Parse(normalized)));
    }

    /// <summary>
    /// An escaped backslash immediately followed by a letter escape: the first is two characters of
    /// content and the second is still decoded, which is what a single scan has to get right.
    /// </summary>
    [Fact]
    public void NormalizingRawJson_DecodesALetterEscapeAfterAnEscapedBackslash()
    {
        var input = "{\"m\":\"" + Backslash + Backslash + Hex(0x0422) + "\"}";

        Assert.Equal("{\"m\":\"" + Backslash + Backslash + "Т\"}", ModelFacingJson.NormalizeRawJson(input));
    }

    [Theory]
    [InlineData("u04")]
    [InlineData("u")]
    [InlineData("")]
    public void NormalizingRawJson_CopiesAnIncompleteEscapeAtTheEndWithoutThrowing(string escapeBody)
    {
        var input = "{\"m\":\"" + Backslash + escapeBody;

        Assert.Equal(input, ModelFacingJson.NormalizeRawJson(input));
    }

    [Theory]
    [InlineData(0xD83D)]
    [InlineData(0xDE00)]
    [InlineData(0x00A0)]
    [InlineData(0x3000)]
    public void NormalizingRawJson_KeepsAnEscapeForASurrogateHalfOrASpaceSeparator(int codeUnit)
    {
        var input = "{\"m\":\"" + Hex(codeUnit) + "\"}";

        Assert.Equal(input, ModelFacingJson.NormalizeRawJson(input));
    }

    [Fact]
    public void NormalizingRawJson_KeepsAQuoteAndABackslashEscapedInsideAPropertyName()
    {
        var input = "{\"" + Hex(0x0422) + Hex(0x0022) + Backslash + Backslash + "\":1}";

        Assert.Equal("{\"Т" + Hex(0x0022) + Backslash + Backslash + "\":1}", ModelFacingJson.NormalizeRawJson(input));
    }

    [Fact]
    public void NormalizingRawJson_RoundTripsToTheSameValues()
    {
        var inputs = new[]
        {
            "{\"message\":\"" + Hex(0x0422) + Hex(0x0430) + "\"}",
            "{\"m\":\"" + Hex(0x0022) + "\"}",
            "{\"m\":\"" + Hex(0x000A) + "\"}",
            "{\"m\":\"" + Hex(0x202E) + Hex(0x200B) + "\"}",
            "{\"m\":\"" + Hex(0xD83D) + Hex(0xDE00) + "\"}",
            "{\"m\":\"" + Backslash + Backslash + "u0422\"}",
            "{\"m\":\"Тайм-аут\"}",
            "{\"m\":\"plain\",\"n\":[{\"k\":\"" + Hex(0x0439) + "\"}]}",
            "{\"m\":\"a" + Ch(0x202E) + Ch(0x2028) + Ch(0x00A0) + Ch(0xFEFF) + "b\"}",
            "{\"m\":\"" + Ch(0x1F600) + Ch(0x2800) + Ch(0x034F) + "\"}",
            "{\"" + Ch(0x043A) + "\":\"" + Ch(0x0430) + "\"}"
        };

        foreach (var input in inputs)
        {
            var normalized = ModelFacingJson.NormalizeRawJson(input);

            Assert.True(
                JsonNode.DeepEquals(JsonNode.Parse(input), JsonNode.Parse(normalized)),
                "Normalizing changed the values of " + input);
        }
    }

    [Fact]
    public void NormalizingAnElement_UsesItsOwnRawText()
    {
        var element = CanonicalJsonSerializer.ToElement(new JsonObject { ["title"] = "Тайм-аут" });

        // The element a canonical writer produced escapes every letter; the model-facing form does not.
        Assert.StartsWith("{\"title\":\"" + Hex(0x0422), element.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("{\"title\":\"Тайм-аут\"}", ModelFacingJson.Normalize(element));
    }
}
