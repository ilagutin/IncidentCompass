using IncidentCompass.Application.Memory;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The script classifier and the eligible-word coverage rule it feeds. For a query and a candidate in
/// one script every counted word stays eligible, so the gate is exactly the half rule it was before;
/// only a word in a script the candidate never writes is left out of the judgement.
/// </summary>
public sealed class MemorySearchScriptCoverageTests
{
    private const string EnglishChunk = MemorySearchToolTestSupport.EnglishChunk;
    private const string RussianChunk = "таймаут оформления заказа из-за склада";

    [Theory]
    [InlineData("checkout", "Latin")]
    [InlineData("żądanie", "Latin")]
    [InlineData("ếch", "Latin")]
    [InlineData("склада", "Cyrillic")]
    [InlineData("σφάλμα", "Greek")]
    [InlineData("خطأ", "Arabic")]
    [InlineData("שגיאה", "Hebrew")]
    [InlineData("超时", "Han")]
    [InlineData("たいむ", "Hiragana")]
    [InlineData("タイム", "Katakana")]
    [InlineData("시간", "Hangul")]
    [InlineData("ᚠᚢᚦ", "Other")]
    [InlineData("504", "Neutral")]
    [InlineData("2.4.0", "Neutral")]
    public void Classify_DecidesTheScriptFromTheFirstLetter(string word, string expected)
    {
        Assert.Equal(expected, WritingScriptClassifier.Classify(word).ToString());
    }

    [Fact]
    public void Classify_IgnoresLeadingDigitsAndUsesTheFirstLetter()
    {
        Assert.Equal(WritingScript.Cyrillic, WritingScriptClassifier.Classify("504ошибка"));
        Assert.Equal(WritingScript.Latin, WritingScriptClassifier.Classify("504timeout"));
    }

    /// <summary>
    /// A supplementary-plane letter is a surrogate pair, and <c>char.IsLetter</c> is false for either
    /// half. Classifying per UTF-16 unit would call such a word letterless and therefore neutral, which
    /// would make it eligible everywhere and unable to signal a foreign script.
    /// </summary>
    [Theory]
    [InlineData("\U00020000", "Han")]
    [InlineData("\U0002A6DF", "Han")]
    [InlineData("\U0002F81A", "Han")]
    [InlineData("\U000323AF", "Han")]
    [InlineData("\U00010330\U00010331", "Other")]
    [InlineData("\U00010400", "Other")]
    public void Classify_SupplementaryPlaneLetterIsNotNeutral(string word, string expected)
    {
        Assert.Equal(expected, WritingScriptClassifier.Classify(word).ToString());
        Assert.NotEqual(WritingScript.Neutral, WritingScriptClassifier.Classify(word));
    }

    [Theory]
    [InlineData("checkout timeout", 2, 2, true)]
    [InlineData("checkout rollback", 2, 1, true)]
    [InlineData("checkout rollback procedure", 3, 1, false)]
    [InlineData("checkout timeout procedure", 3, 2, true)]
    [InlineData("checkout rollback procedure legacy", 4, 1, false)]
    [InlineData("checkout timeout rollback legacy", 4, 2, true)]
    public void Evaluate_EnglishOverEnglishKeepsExactlyTheHalfRule(
        string query,
        int expectedCounted,
        int expectedMatched,
        bool expectedSupported)
    {
        var support = MemorySearchLexicalFilter.Evaluate(query, EnglishChunk);

        Assert.Equal(expectedCounted, support.CountedQueryWords);
        Assert.Equal(expectedCounted, support.EligibleQueryWords);
        Assert.Equal(expectedMatched, support.MatchedQueryWords);
        Assert.Equal(expectedSupported, support.IsSupported);
    }

    [Fact]
    public void Evaluate_MixedScriptQueryIsJudgedOnItsEligibleWordsOnly()
    {
        var support = MemorySearchLexicalFilter.Evaluate("circuit breaker при задержках склада", EnglishChunk);

        Assert.Equal(5, support.CountedQueryWords);
        Assert.Equal(2, support.EligibleQueryWords);
        Assert.Equal(2, support.MatchedQueryWords);
        Assert.True(support.IsSupported);
        Assert.False(support.IsFullyCovered);
        Assert.Equal(MemoryRetrievalConfidence.Medium, MemoryRetrievalConfidence.Band(support, vectorOnly: false));
    }

    [Fact]
    public void Evaluate_ForeignScriptOnlyQueryHasNoEligibleWordAndIsNotSupported()
    {
        var support = MemorySearchLexicalFilter.Evaluate("таймаут оформления заказа", EnglishChunk);

        Assert.Equal(3, support.CountedQueryWords);
        Assert.Equal(0, support.EligibleQueryWords);
        Assert.Equal(0, support.MatchedQueryWords);
        Assert.False(support.IsSupported);
        Assert.Equal(0, support.Coverage);
    }

    [Fact]
    public void Evaluate_DigitsAreScriptNeutralAndStayEligibleAcrossScripts()
    {
        var support = MemorySearchLexicalFilter.Evaluate("circuit 504", "504 " + RussianChunk);

        Assert.Equal(2, support.CountedQueryWords);
        Assert.Equal(1, support.EligibleQueryWords);
        Assert.Equal(1, support.MatchedQueryWords);
        Assert.True(support.IsSupported);
        Assert.False(support.IsFullyCovered);
    }

    [Fact]
    public void Evaluate_FullCoverageOfEveryCountedWordIsTheHighBand()
    {
        var support = MemorySearchLexicalFilter.Evaluate("checkout timeout inventory", EnglishChunk);

        Assert.True(support.IsFullyCovered);
        Assert.Equal(MemoryRetrievalConfidence.High, MemoryRetrievalConfidence.Band(support, vectorOnly: false));
        Assert.Equal(MemoryRetrievalConfidence.Low, MemoryRetrievalConfidence.Band(support, vectorOnly: true));
    }

    [Fact]
    public void Evaluate_ForeignScriptQueryOverItsOwnCorpusIsJudgedOnEveryWord()
    {
        var support = MemorySearchLexicalFilter.Evaluate("таймаут оформления заказа", RussianChunk);

        Assert.Equal(3, support.CountedQueryWords);
        Assert.Equal(3, support.EligibleQueryWords);
        Assert.Equal(3, support.MatchedQueryWords);
        Assert.True(support.IsFullyCovered);
    }

    [Fact]
    public void Coverage_UsesTheEligibleWordDenominatorTheGateJudgesOn()
    {
        Assert.Equal(1.0, MemorySearchLexicalFilter.Coverage("circuit breaker при задержках склада", EnglishChunk));
        Assert.Equal(0.5, MemorySearchLexicalFilter.Coverage("checkout rollback", EnglishChunk));
        Assert.Equal(0, MemorySearchLexicalFilter.Coverage("таймаут оформления заказа", EnglishChunk));
    }

    [Fact]
    public void CountedScripts_ReportsOnlyTheScriptsOfCountedWords()
    {
        Assert.Equal([WritingScript.Latin], MemorySearchLexicalFilter.CountedScripts(EnglishChunk));
        Assert.Equal(
            [WritingScript.Latin, WritingScript.Cyrillic],
            MemorySearchLexicalFilter.CountedScripts("circuit breaker при задержках склада").Order());
    }
}
