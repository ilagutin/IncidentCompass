namespace IncidentCompass.Application.Memory;

/// <summary>
/// What <c>memory_search</c> does when lexical coverage leaves no candidate at all. Configured as
/// <c>Tools.memory_search.VectorOnlyFallback</c>; it never applies while the lexical gate keeps
/// something, and it never reaches below <c>MinScore</c>, which the repository already enforced.
/// </summary>
internal enum MemorySearchVectorOnlyFallback
{
    /// <summary>Return nothing, the behaviour before the setting existed.</summary>
    Off,

    /// <summary>
    /// Return vector-only matches only when the query carries a counted word in a script no candidate
    /// writes. A query in the corpus's own script that simply matches nothing still returns nothing.
    /// </summary>
    ForeignScript,

    /// <summary>Return vector-only matches whenever the lexical gate left none.</summary>
    Always
}
