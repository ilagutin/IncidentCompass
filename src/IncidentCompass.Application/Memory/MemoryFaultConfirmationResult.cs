namespace IncidentCompass.Application.Memory;

/// <summary>
/// The banded matches, and the limitation the confirmation step itself has to report, which is set
/// only when the judge answered the admission call and was absent for the confirmation call. Every
/// other reason nothing was confirmed is already carried by the admission judgement's limitation.
/// </summary>
internal sealed record MemoryFaultConfirmationResult(
    IReadOnlyList<MemorySearchRankedMatch> Matches,
    string? Limitation);
