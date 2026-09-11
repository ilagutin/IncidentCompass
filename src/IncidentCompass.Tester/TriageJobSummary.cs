namespace IncidentCompass.Tester;

// The Tester keeps its own copy of the fault endpoint's job summary because it speaks to the API as
// a black box. A copy only sees what it declares: every property it omits is silently dropped while
// deserializing, so a field the API added is invisible here until this record names it.
//
// LastErrorCode is the backend's own bounded classification of the job's most recent failed attempt,
// from a closed application-owned vocabulary, and is null when no attempt has failed. NextAttemptAtUtc
// is when a delayed job becomes claimable again, and is null when no retry is scheduled; the backend
// clears it when it dead-letters a job, so a terminal observation always reads it as null. Both are
// optional parameters so a caller that has neither, such as a test stub for a job that never failed,
// can still name the summary's original five facts positionally.
internal sealed record TriageJobSummary(
    Guid Id,
    string Status,
    int Attempt,
    string ConfigHash,
    DateTimeOffset CreatedAtUtc,
    string? LastErrorCode = null,
    DateTimeOffset? NextAttemptAtUtc = null);
