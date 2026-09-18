using IncidentCompass.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Relevance;

/// <summary>
/// Refuses a relevance-judge provider the Worker cannot compose, at start. Only the two kinds
/// <see cref="RelevanceJudgeOptions" /> names are judges; an OpenAI-compatible server is a chat and
/// embedding provider and has no relevance judge behind it.
/// </summary>
internal sealed class RelevanceJudgeOptionsValidator : IValidateOptions<RelevanceJudgeOptions>
{
    public ValidateOptionsResult Validate(string? name, RelevanceJudgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return IsSupported(options.Provider)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                "Relevance judge provider '" + options.Provider + "' is unsupported; " +
                RelevanceJudgeOptions.ProviderKey + " must be " + RelevanceJudgeOptions.LocalOnnxProvider +
                " or " + RelevanceJudgeOptions.MockProvider + ".");
    }

    public static bool IsSupported(string? provider) =>
        ProviderKindParser.TryParse(provider, out var kind) &&
        kind is ProviderKind.LocalOnnx or ProviderKind.Mock;

    public static bool IsMock(string? provider) =>
        ProviderKindParser.TryParse(provider, out var kind) && kind == ProviderKind.Mock;
}
