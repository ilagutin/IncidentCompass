using System.Text.Json;

namespace IncidentCompass.Tester.Evaluation;

internal sealed record EvaluationCorpus(
    int SchemaVersion,
    string CorpusVersion,
    IReadOnlyList<EvaluationCase> Cases)
{
    public const int RequiredAttemptsPerCase = 3;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> RequiredKinds = new(StringComparer.Ordinal)
    {
        "known",
        "unknown",
        "insufficient",
        "stale",
        "adversarial"
    };

    public static EvaluationCorpus Load(string path)
    {
        var corpus = JsonSerializer.Deserialize<EvaluationCorpus>(
            File.ReadAllText(path),
            SerializerOptions)
            ?? throw new InvalidOperationException("Evaluation corpus is empty.");
        corpus.Validate();
        return corpus;
    }

    public void Validate()
    {
        if (SchemaVersion != 1 || CorpusVersion != "triage-evaluation-corpus-v1")
        {
            throw new InvalidOperationException("Unsupported triage evaluation corpus contract.");
        }

        if (Cases.Count != RequiredKinds.Count ||
            !Cases.Select(static item => item.Kind).ToHashSet(StringComparer.Ordinal).SetEquals(RequiredKinds) ||
            Cases.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() != Cases.Count)
        {
            throw new InvalidOperationException("Evaluation corpus must contain exactly one uniquely named case for every required kind.");
        }

        foreach (var item in Cases)
        {
            ValidateCase(item);
        }
    }

    private static void ValidateCase(EvaluationCase item)
    {
        if (string.IsNullOrWhiteSpace(item.Id) ||
            string.IsNullOrWhiteSpace(item.Input.ServiceName) ||
            string.IsNullOrWhiteSpace(item.Input.ErrorType) ||
            string.IsNullOrWhiteSpace(item.Input.ErrorMessage) ||
            item.Input.AttemptKeys.Count != RequiredAttemptsPerCase ||
            item.Input.AttemptKeys.Any(static key => string.IsNullOrWhiteSpace(key)) ||
            item.Input.AttemptKeys.Distinct(StringComparer.Ordinal).Count() != RequiredAttemptsPerCase ||
            item.Criteria.Diagnosis.AllowedClassifications.Count == 0 ||
            item.Criteria.Diagnosis.RequiredAnyTerms.Count == 0 ||
            item.Criteria.Refusal.AllowedStatuses.Count == 0)
        {
            throw new InvalidOperationException("Every evaluation case must freeze its input and authored diagnosis and refusal criteria.");
        }

        var tolerance = item.Criteria.Tolerance;
        if (tolerance.MinimumCompletionPasses < 0 ||
            tolerance.MinimumDiagnosisPasses < 0 ||
            tolerance.MinimumEvidencePasses < 0 ||
            tolerance.MinimumRefusalPasses < 0 ||
            tolerance.MinimumSafetyPasses <= 0 ||
            tolerance.MinimumCompletionPasses > RequiredAttemptsPerCase ||
            tolerance.MinimumDiagnosisPasses > RequiredAttemptsPerCase ||
            tolerance.MinimumEvidencePasses > RequiredAttemptsPerCase ||
            tolerance.MinimumRefusalPasses > RequiredAttemptsPerCase ||
            tolerance.MinimumSafetyPasses > RequiredAttemptsPerCase)
        {
            throw new InvalidOperationException("Every evaluation tolerance count must fit the frozen three-attempt contract, with a positive safety count.");
        }

        if (item.Criteria.Evidence.MinimumCitations < 0 || item.Criteria.Refusal.MinimumLimitations < 0)
        {
            throw new InvalidOperationException("Evaluation evidence and refusal minimums cannot be negative.");
        }
    }
}
