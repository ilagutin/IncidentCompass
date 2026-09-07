namespace IncidentCompass.Tester.Evaluation;

internal static class EvaluationSummaryBuilder
{
    public static EvaluationRunResult Build(
        EvaluationCorpus corpus,
        EvaluationOptions options,
        DateTimeOffset startedAtUtc,
        IReadOnlyList<EvaluationAttemptResult> attempts)
    {
        var summaries = corpus.Cases.Select(item => SummarizeCase(item, options.RunsPerCase, attempts)).ToArray();
        var requested = corpus.Cases.Count * options.RunsPerCase;
        return new EvaluationRunResult(
            2,
            corpus.CorpusVersion,
            options.EvaluatedRevision,
            options.EvaluatedContentIdentity,
            options.EvaluatedContentDirty,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            options.RunsPerCase,
            new EvaluationSettingsResult(
                options.Configuration.Routes,
                options.Configuration.OrchestratorBudget,
                options.EmptyActionGrants,
                options.ExternalActionCredentialsProvided),
            attempts,
            summaries,
            new EvaluationMetricSummary(
                requested,
                attempts.Count,
                attempts.Count(static item => item.FailureDetail is not null || !item.Completion.Passed),
                attempts.Count(static item => item.Usage.TotalTokens.HasValue),
                attempts.Count(static item => item.Usage.Availability != "available"),
                Range(attempts.Select(static item => (double)item.Latency.EndToEndMilliseconds)),
                Range(attempts.Where(static item => item.Latency.ModelCallTotalMilliseconds.HasValue)
                    .Select(static item => (double)item.Latency.ModelCallTotalMilliseconds!.Value)),
                Range(attempts.Where(static item => item.Usage.TotalTokens.HasValue)
                    .Select(static item => (double)item.Usage.TotalTokens!.Value))),
            attempts.Count == requested && summaries.All(static item => item.TolerancePassed),
            "Descriptive small-sample measurement only. Diagnosis keyword matching is an authored heuristic, not proof of diagnostic correctness. Grounding proves provenance, not diagnostic truth; adversarial safety records configured backend authority and one observed run, not universal prompt-injection resistance.");
    }

    private static EvaluationCaseSummary SummarizeCase(
        EvaluationCase item,
        int requested,
        IReadOnlyList<EvaluationAttemptResult> attempts)
    {
        var observed = attempts.Where(result => result.CaseId == item.Id).ToArray();
        var tolerance = item.Criteria.Tolerance;
        var completion = observed.Count(static result => result.Completion.Passed);
        var diagnosis = observed.Count(static result => result.Diagnosis.Passed);
        var evidence = observed.Count(static result => result.Evidence.Passed);
        var refusal = observed.Count(static result => result.JustifiedRefusal.Passed);
        var safety = observed.Count(static result => result.BackendActionSafety.Passed);
        return new EvaluationCaseSummary(
            item.Id,
            item.Kind,
            requested,
            observed.Length,
            completion,
            diagnosis,
            evidence,
            refusal,
            safety,
            observed.Length == requested &&
            completion >= tolerance.MinimumCompletionPasses &&
            diagnosis >= tolerance.MinimumDiagnosisPasses &&
            evidence >= tolerance.MinimumEvidencePasses &&
            refusal >= tolerance.MinimumRefusalPasses &&
            safety >= tolerance.MinimumSafetyPasses);
    }

    private static EvaluationRange? Range(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        var middle = ordered.Length / 2;
        var median = ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
        return new EvaluationRange(ordered[0], median, ordered[^1]);
    }
}
