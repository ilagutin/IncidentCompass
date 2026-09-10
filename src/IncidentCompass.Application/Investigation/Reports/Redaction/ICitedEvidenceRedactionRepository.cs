namespace IncidentCompass.Application.Investigation.Reports.Redaction;

/// <summary>
/// Reads what the redaction boundary recorded for the artifacts a report cites, so publication can
/// state a withholding the model was never asked about.
/// </summary>
public interface ICitedEvidenceRedactionRepository
{
    /// <summary>
    /// Returns one entry per artifact that resolves within this job attempt. Reference ids that do
    /// not name such an artifact are skipped rather than rejected: publication validates citations
    /// separately and fails there, and this read exists only to decide a marker.
    /// <para>
    /// Every reference id has to be looked at. An implementation must not truncate the list, sample
    /// it or stop early, because the model authors and orders its own evidence array: an answer that
    /// covers only part of it is one the model can steer, and the only direction it can be steered
    /// is towards saying nothing was withheld.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<CitedEvidenceRedaction>> ReadCitedAsync(
        Guid jobId,
        int attempt,
        IReadOnlyCollection<string> referenceIds,
        CancellationToken cancellationToken);
}
