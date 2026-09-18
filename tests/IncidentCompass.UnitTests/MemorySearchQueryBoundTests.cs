using System.Text.Json;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Application.Memory;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The judge scores the query and the candidate inside one token window, so an unbounded query is a
/// query the judge never sees in full, and before this bound existed a long enough one left the
/// passage no room at all: every candidate then scored against an empty passage and received the
/// same score, so the judge admitted all of them or none whatever they contained. The encoder no
/// longer lets that happen, and the tool refuses the call rather than silently truncating it.
/// </summary>
public sealed class MemorySearchQueryBoundTests
{
    [Fact]
    public void Validate_AQueryFarOverTheBound_IsRefusedAsInvalidArguments()
    {
        var result = Validate(new string('q', MemorySearchQueryBound.MaxQueryCharacters * 4));

        Assert.False(result.IsValid);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Equal(MemorySearchQueryBound.TooLongRefusal, result.ErrorMessage);
    }

    [Fact]
    public void Validate_AQueryOneCharacterOverTheBound_IsRefused()
    {
        Assert.False(Validate(new string('q', MemorySearchQueryBound.MaxQueryCharacters + 1)).IsValid);
    }

    [Fact]
    public void Validate_AQueryExactlyAtTheBound_IsAccepted()
    {
        var query = new string('q', MemorySearchQueryBound.MaxQueryCharacters);

        var result = Validate(query);

        Assert.True(result.IsValid);
        Assert.Equal(query, result.SanitizedArguments.GetProperty("query").GetString());
    }

    /// <summary>
    /// The length is measured on the trimmed query, the same text the tool goes on to use, so
    /// surrounding whitespace cannot refuse a call that is within the bound.
    /// </summary>
    [Fact]
    public void Validate_MeasuresTheTrimmedQuery()
    {
        var query = new string('q', MemorySearchQueryBound.MaxQueryCharacters);

        var result = Validate("   " + query + "\t\n");

        Assert.True(result.IsValid);
        Assert.Equal(query, result.SanitizedArguments.GetProperty("query").GetString());
    }

    /// <summary>
    /// The bound is derived from the judge's own window rather than picked: half the content budget
    /// of the pinned 512-token window, at the same conservative characters-per-token estimate this
    /// product counts tokens with everywhere else.
    /// </summary>
    [Fact]
    public void TheBound_IsDerivedFromTheJudgesTokenWindow()
    {
        Assert.Equal(254, MemorySearchQueryBound.MaxQueryTokens);
        Assert.Equal(
            (MemorySearchQueryBound.JudgeTokenWindow - MemorySearchQueryBound.PairMarkerTokens) / 2,
            MemorySearchQueryBound.MaxQueryTokens);
        Assert.Equal(
            MemorySearchQueryBound.MaxQueryTokens * MemorySearchQueryBound.CharactersPerToken,
            MemorySearchQueryBound.MaxQueryCharacters);
        Assert.Contains(
            MemorySearchQueryBound.MaxQueryCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture),
            MemorySearchQueryBound.TooLongRefusal,
            StringComparison.Ordinal);
    }

    private static ToolValidationResult Validate(string query)
    {
        var tool = new MemorySearchTool(new StubEmbeddingClient(), new StubMemoryRepository([]));
        using var arguments = JsonSerializer.SerializeToDocument(new { query });
        return tool.Validate(arguments.RootElement);
    }
}
