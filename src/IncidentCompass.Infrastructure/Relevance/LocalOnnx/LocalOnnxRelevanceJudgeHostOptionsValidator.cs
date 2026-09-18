using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// Validates the local relevance-judge settings while the host starts, on a host that configures a
/// judge model directory.
/// <para>
/// <see cref="LocalOnnxRelevanceJudgeOptionsValidator" /> requires that directory and has no gate of
/// its own, which is what an operator command wants: it asks whether this host can manage a judge,
/// and a blank directory is the answer. Start-up is the other question, and a judge has no provider
/// setting to answer it with, so the configured directory is what says whether this host runs one. A
/// host that names a directory has every other judge setting checked here before it starts. A host
/// that names none starts without a judge and refuses every judge call with
/// <c>memory_relevance_judge_unavailable</c>, rather than failing to start over a model it was never
/// asked to run; <c>memory model status</c> reports that state and exits non-zero, so it is visible
/// rather than silent.
/// </para>
/// </summary>
internal sealed class LocalOnnxRelevanceJudgeHostOptionsValidator : IValidateOptions<LocalOnnxRelevanceJudgeOptions>
{
    public ValidateOptionsResult Validate(string? name, LocalOnnxRelevanceJudgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ModelDirectory))
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = LocalOnnxRelevanceJudgeOptionsValidator.FindFailures(options);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
