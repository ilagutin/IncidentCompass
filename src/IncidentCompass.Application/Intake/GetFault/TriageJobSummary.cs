namespace IncidentCompass.Application.Intake.GetFault;

/// <summary>
/// The caller-visible state of the fault's current triage job.
/// </summary>
/// <remarks>
/// <c>Status</c> stays the authority on whether the job is still going to run: <c>Pending</c>,
/// <c>Processing</c> and <c>RetryPending</c> are live states, and only <c>Succeeded</c> and
/// <c>DeadLettered</c> are terminal.
/// <para>
/// <c>LastErrorCode</c> is the durable classification token of the job's most recently recorded
/// attempt outcome, or <see langword="null"/> when no attempt has failed yet. It is an
/// application-owned code from a closed vocabulary (for example <c>provider_unavailable</c>,
/// <c>triage_governance_denied</c> or <c>triage_job_attempt_failed</c>), never provider text: the
/// sibling <c>last_error_message</c> column, whose ordinary form embeds the raising exception's type
/// name, is deliberately not projected. A populated code on a non-terminal status says why the job
/// is waiting, not that it has failed.
/// </para>
/// <para>
/// <c>NextAttemptAtUtc</c> is when a delayed job becomes eligible to be claimed again, or
/// <see langword="null"/> when no retry is scheduled. It is set only while the job is
/// <c>RetryPending</c>; claiming the job and dead-lettering it both clear it.
/// </para>
/// </remarks>
public sealed record TriageJobSummary(
    Guid Id,
    string Status,
    int Attempt,
    string ConfigHash,
    DateTimeOffset CreatedAtUtc,
    string? LastErrorCode,
    DateTimeOffset? NextAttemptAtUtc);
