using System.Globalization;

namespace IncidentCompass.Application.Memory;

/// <summary>
/// The longest query <c>memory_search</c> accepts, and where that length comes from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a bound exists at all.</b> The relevance judge scores one (query, passage) pair inside a
/// fixed token window, so the query and the candidate compete for the same room. The adapter splits
/// that window rather than letting either side starve, but a query written long enough to be cut
/// every time is still a query the judge never sees in full, and the model gains nothing by writing
/// one. Refusing it at the tool boundary makes that a stated contract instead of a silent
/// truncation, and it is refused as <c>invalid_arguments</c> like every other malformed call.
/// </para>
/// <para>
/// <b>Where the number comes from.</b> It is derived from the judge's token window rather than
/// chosen: the pinned cross-encoder's window is <see cref="JudgeTokenWindow" /> tokens, a pair costs
/// <see cref="PairMarkerTokens" /> of them for its markers, the query may occupy at most half of what
/// is left, and a token is estimated at <see cref="CharactersPerToken" /> characters, the same
/// conservative figure the memory chunker's character-estimate token counter uses. That gives
/// <see cref="MaxQueryCharacters" /> characters.
/// </para>
/// <para>
/// <b>Why Application restates the window.</b> The window belongs to the adapter's model, and the
/// port deliberately does not expose it: a host may run another cross-encoder, or none. Restating
/// the pinned value here is what lets the tool refuse before any adapter is reached, and it is safe
/// to be wrong in either direction, because the adapter still cuts on token boundaries inside
/// whatever window it really has. A wider window only means an accepted query is never cut; a
/// narrower one means it is cut to that window's own half, exactly as an unbounded query would have
/// been. A unit test pins this constant against the encoder's own half-budget at the pinned window.
/// </para>
/// </remarks>
internal static class MemorySearchQueryBound
{
    /// <summary>The pinned cross-encoder's token window, and the shipped default of the judge's <c>MaxTokens</c>.</summary>
    public const int JudgeTokenWindow = 512;

    /// <summary>The one opening and three separating or closing markers a scored pair carries.</summary>
    public const int PairMarkerTokens = 4;

    /// <summary>The conservative characters-per-token estimate used wherever this product counts tokens from text.</summary>
    public const int CharactersPerToken = 4;

    /// <summary>Half the content budget of the pinned window, which is all the query may occupy.</summary>
    public const int MaxQueryTokens = (JudgeTokenWindow - PairMarkerTokens) / 2;

    /// <summary>The bound, in characters of the trimmed query.</summary>
    public const int MaxQueryCharacters = MaxQueryTokens * CharactersPerToken;

    /// <summary>
    /// The refusal, which names the bound and echoes nothing the model wrote. It is built from
    /// <see cref="MaxQueryCharacters" /> rather than repeating the number, so the sentence and the
    /// check cannot disagree.
    /// </summary>
    public static readonly string TooLongRefusal =
        "memory_search query must be at most " +
        MaxQueryCharacters.ToString(CultureInfo.InvariantCulture) + " characters.";
}
