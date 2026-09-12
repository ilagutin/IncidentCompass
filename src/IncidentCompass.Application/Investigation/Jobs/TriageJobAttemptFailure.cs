using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Investigation.Jobs;

public sealed record TriageJobAttemptFailure(
    TriageJobStatus Status,
    string ErrorCode,
    string ErrorMessage,
    DateTimeOffset? NextAttemptAtUtc,
    TriageJobRetryBudgetDisposition RetryBudgetDisposition = TriageJobRetryBudgetDisposition.ConsumeAttempt,
    InvestigationModelCallAccounting? ModelCallAccounting = null);
