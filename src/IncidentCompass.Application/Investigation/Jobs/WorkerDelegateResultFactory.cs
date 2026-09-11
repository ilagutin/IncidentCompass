using System.Text.Json;

namespace IncidentCompass.Application.Investigation.Jobs;

internal static class WorkerDelegateResultFactory
{
    private const string MemoryRoleName = "memory";

    public static WorkerDelegateResult Create(
        string roleName,
        string workerContent,
        Guid workerOutputArtifactId)
    {
        using var document = JsonDocument.Parse(workerContent);
        return IsMemoryOutput(roleName, document.RootElement)
            ? CreateMemoryResult(roleName, workerContent, workerOutputArtifactId)
            : CreateAnalysisResult(roleName, workerContent, workerOutputArtifactId);
    }

    private static bool IsMemoryOutput(string roleName, JsonElement root)
    {
        return string.Equals(roleName, MemoryRoleName, StringComparison.Ordinal) ||
            root.TryGetProperty("matched", out _);
    }

    private static WorkerDelegateResult CreateAnalysisResult(
        string roleName,
        string workerContent,
        Guid workerOutputArtifactId)
    {
        var output = AnalysisWorkerOutputParser.Parse(workerContent, roleName);
        var rationale = output.Rationale ?? string.Join(" ", output.KeyFacts);
        return new WorkerDelegateResult(
            rationale,
            JsonSerializer.Serialize(new
            {
                role = roleName,
                summary = rationale,
                keyFacts = output.KeyFacts,
                candidateClassification = output.CandidateClassification,
                needsDeeperContext = output.NeedsDeeperContext,
                artifactId = workerOutputArtifactId
            }));
    }

    /// <summary>
    /// Items are projected field by field rather than serialized as records. Two reasons, and both
    /// are about the orchestrator being able to act on what it is handed: the projection is the only
    /// place that decides which of a retrieved document's fields the orchestrator sees, and it names
    /// them in the same camel case the shipped instructions and <c>memory_search</c> itself use, so
    /// the key the orchestrator is told to read is the key that arrives. Serializing the record
    /// instead emitted PascalCase names that matched nothing the model had been shown.
    /// </summary>
    private static WorkerDelegateResult CreateMemoryResult(
        string roleName,
        string workerContent,
        Guid workerOutputArtifactId)
    {
        var output = MemoryWorkerOutputParser.Parse(workerContent);
        return new WorkerDelegateResult(
            output.Rationale,
            JsonSerializer.Serialize(new
            {
                role = roleName,
                summary = output.Rationale,
                matched = output.Matched,
                items = output.Items.Select(static item => new
                {
                    artifactId = item.ArtifactId,
                    title = item.Title,
                    quote = item.Quote,
                    score = item.Score,
                    documentationStatus = item.DocumentationStatus
                }),
                noMatchReason = output.NoMatchReason,
                artifactId = workerOutputArtifactId
            }));
    }
}
