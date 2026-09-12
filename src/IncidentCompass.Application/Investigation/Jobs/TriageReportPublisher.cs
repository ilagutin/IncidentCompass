using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Investigation.Reports;
using IncidentCompass.Application.Investigation.Reports.Context;
using IncidentCompass.Application.Investigation.Reports.Fallback;
using IncidentCompass.Application.Investigation.Reports.Redaction;
using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Investigation.Jobs;

internal sealed class TriageReportPublisher(
    ITriageReportRepository reportRepository,
    IReadOnlyContextOutcomeRepository contextOutcomeRepository,
    ICitedEvidenceRedactionRepository citedEvidenceRedactionRepository,
    IAttemptModelFallbackRepository attemptModelFallbackRepository)
{
    public async Task PublishAsync(
        TriageJob job,
        string workerId,
        AiToolCall toolCall,
        CancellationToken cancellationToken)
    {
        var report = TriageReportParser.Parse(toolCall.Arguments);
        var contextOutcomes = await contextOutcomeRepository.ReadCurrentAttemptAsync(
            job.Id,
            job.Attempt,
            cancellationToken);
        report = ContextOutcomeReportPolicy.Apply(report, contextOutcomes);

        // Runs after the context-outcome policy so that the reserved redaction sentence is settled
        // last: whatever the model wrote into its own limitations, what stands here is what the
        // backend derived.
        var citedEvidence = await citedEvidenceRedactionRepository.ReadCitedAsync(
            job.Id,
            job.Attempt,
            report.Evidence.Select(evidence => evidence.ReferenceId).ToArray(),
            cancellationToken);
        report = EvidenceRedactionReportPolicy.Apply(report, citedEvidence);

        // Each reserved-sentence policy settles its own sentence and touches no other, so the order
        // between them is not load-bearing; both run after the model's own limitations are parsed so
        // that what stands is what the backend derived.
        var fallbacks = await attemptModelFallbackRepository.ReadCurrentAttemptAsync(
            job.Id,
            job.Attempt,
            cancellationToken);
        report = ModelFallbackReportPolicy.Apply(report, fallbacks);
        await reportRepository.PublishAsync(job, workerId, report, cancellationToken);
    }
}
