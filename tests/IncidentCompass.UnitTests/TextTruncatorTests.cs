using IncidentCompass.Application.Core.Text;
using static IncidentCompass.UnitTests.ModelFacingJsonText;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The shared text cut. It bounds a value by UTF-16 code units, it never leaves half of a surrogate
/// pair behind, and for text with no pair straddling the cut it cuts exactly where it always did.
/// </summary>
public sealed class TextTruncatorTests
{
    /// <summary>One emoji: two code units, so it is the shortest text that can straddle a cut.</summary>
    private static readonly string Pair = Ch(0x1F600);

    [Theory]
    // Shorter than the cap, equal to it, longer than it - with and without a suffix.
    [InlineData("abc", 5, "", "abc")]
    [InlineData("abcde", 5, "", "abcde")]
    [InlineData("abcdefg", 5, "", "abcde")]
    [InlineData("abc", 5, "...", "abc")]
    [InlineData("abcde", 5, "...", "abcde")]
    [InlineData("abcdefg", 5, "...", "ab...")]
    [InlineData("abcdefg", 4, "...", "a...")]
    public void Truncate_WithoutASurrogatePairAtTheCut_CutsWhereItAlwaysDid(
        string value,
        int maxLength,
        string suffix,
        string expected)
    {
        var truncated = TextTruncator.Truncate(value, maxLength, suffix);

        Assert.Equal(expected, truncated);
        Assert.True(truncated.Length <= maxLength);
    }

    [Fact]
    public void Truncate_PairStraddlingTheCut_DropsThePairWhole()
    {
        // "ab" then the pair at indexes 2 and 3: a cut at 3 would keep the high surrogate alone.
        var value = "ab" + Pair + "cd";

        var truncated = TextTruncator.Truncate(value, 3);

        Assert.Equal("ab", truncated);
        Assert.DoesNotContain(Pair, truncated, StringComparison.Ordinal);
        Assert.DoesNotContain(truncated, static character => char.IsSurrogate(character));
        Assert.True(truncated.Length <= 3);
    }

    [Fact]
    public void Truncate_PairStraddlingTheCutWithASuffix_DropsThePairWholeAndStaysUnderTheCap()
    {
        // The suffix takes three of the six units, so the cut lands at 3, inside the pair again.
        var value = "ab" + Pair + "cdef";

        var truncated = TextTruncator.Truncate(value, 6, "...");

        Assert.Equal("ab...", truncated);
        Assert.True(truncated.Length <= 6);
    }

    [Fact]
    public void Truncate_PairEndingExactlyAtTheCut_KeepsIt()
    {
        var value = "ab" + Pair + "cd";

        Assert.Equal("ab" + Pair, TextTruncator.Truncate(value, 4));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Truncate_TextMadeOnlyOfPairs_NeverExceedsTheCapAndNeverEndsOnASurrogate(int maxLength)
    {
        var value = string.Concat(Enumerable.Repeat(Pair, 8));

        var truncated = TextTruncator.Truncate(value, maxLength);

        Assert.True(truncated.Length <= maxLength, $"length {truncated.Length} exceeds {maxLength}");
        // Only whole pairs survive, so the length is even and no lone surrogate is left at the end.
        Assert.Equal(0, truncated.Length % 2);
        Assert.False(truncated.Length > 0 && char.IsHighSurrogate(truncated[^1]));
    }

    [Fact]
    public void Truncate_RejectsASuffixThatLeavesNoRoom()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextTruncator.Truncate("abcdef", 3, "..."));
    }
}
