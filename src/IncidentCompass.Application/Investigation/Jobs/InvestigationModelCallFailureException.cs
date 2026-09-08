namespace IncidentCompass.Application.Investigation.Jobs;

internal sealed class InvestigationModelCallFailureException(
    InvestigationModelCallAccounting accounting,
    Exception innerException)
    : Exception("Durable investigation model-call accounting is pending.", innerException)
{
    public InvestigationModelCallAccounting Accounting { get; } = accounting;
}
