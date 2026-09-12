using System.Text.Json;

namespace IncidentCompass.Tester.Evaluation;

internal static class EvaluationCheckpointWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task WriteAsync(
        EvaluationCorpus corpus,
        EvaluationOptions options,
        DateTimeOffset startedAtUtc,
        IReadOnlyList<EvaluationAttemptResult> attempts,
        CancellationToken cancellationToken)
    {
        var result = EvaluationSummaryBuilder.Build(corpus, options, startedAtUtc, attempts);
        var outputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(result, SerializerOptions),
                cancellationToken);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
