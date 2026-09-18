using IncidentCompass.Application.Memory;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The shipped memory role instructions quote the tool's own sentences verbatim, and a role told to
/// recognize a sentence the tool no longer emits recognizes nothing. Nothing else pins them together:
/// the mock scripts and the evaluation client branch on <c>retrievalConfidence</c>, so they stay
/// correct when a message changes, and the instructions do not.
/// </summary>
public sealed class MemoryInstructionLiteralTests
{
    [Theory]
    [InlineData(MemorySearchMessage.MatchesFound)]
    [InlineData(MemorySearchMessage.RelatedMatches)]
    [InlineData(MemorySearchMessage.VectorOnlyMatches)]
    [InlineData(MemorySearchMessage.NoMatches)]
    public void ShippedMemoryInstructions_QuoteEveryToolMessageVerbatim(string message)
    {
        Assert.Contains(message, ReadInstructions(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The instruction file is prose and wraps, so a message long enough to be split across two lines
    /// would not be found by a plain search. Reading it with its line breaks folded to single spaces is
    /// what makes the assertion above a real check rather than one that silently starts passing on the
    /// short messages alone.
    /// </summary>
    [Fact]
    public void ShippedMemoryInstructions_AreCheckedWithTheirLineBreaksFolded()
    {
        var text = ReadInstructions();

        Assert.DoesNotContain('\n', text);
        Assert.Contains(MemorySearchMessage.RelatedMatches, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every message the tool can report is covered by a case above. A new message that reached the
    /// tool without reaching the instructions fails here rather than at a role that cannot read it.
    /// </summary>
    [Fact]
    public void EveryToolMessageIsPinned()
    {
        var text = ReadInstructions();

        Assert.Equal(4, MemorySearchMessage.All.Count);
        Assert.All(MemorySearchMessage.All, message =>
            Assert.Contains(message, text, StringComparison.Ordinal));
    }

    private static string ReadInstructions()
    {
        var path = Path.Combine(RepositoryRootLocator.Find(), "config", "instructions", "memory.md");
        return File.ReadAllText(path)
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }
}
