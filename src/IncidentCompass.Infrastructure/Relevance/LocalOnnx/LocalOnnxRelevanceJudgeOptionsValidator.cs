using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

/// <summary>
/// Validates the local relevance-judge settings, following the same rules
/// <c>LocalOnnxEmbeddingOptionsValidator</c> applies, minus every embedding-only one: a cross-encoder
/// has no vector width, no pooling and no prefixes to check.
/// <para>
/// There is no provider gate here, because a judge has no provider setting of its own: the host
/// registers this validator only where it composes the judge, and a host that does not compose one
/// never binds the section.
/// </para>
/// </summary>
internal sealed class LocalOnnxRelevanceJudgeOptionsValidator : IValidateOptions<LocalOnnxRelevanceJudgeOptions>
{
    public const int MaxTokenWindow = 8192;
    public const int MaxIntraOpThreads = 16;
    public const int MaxInstallTimeoutSeconds = 7200;
    public const long MaxDownloadBytesLimit = 16L << 30;

    /// <summary>
    /// The shortest usable window: the four sequence markers plus one content token. The manifest's
    /// own floor of three is an embedding sequence's floor and would leave a pair no room at all.
    /// </summary>
    public const int MinTokenWindow = LocalOnnxPairEncoder.SpecialTokenCount + 1;

    public ValidateOptionsResult Validate(string? name, LocalOnnxRelevanceJudgeOptions options)
    {
        var failures = FindFailures(options);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<string> FindFailures(LocalOnnxRelevanceJudgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ModelDirectory) || !Path.IsPathFullyQualified(options.ModelDirectory))
        {
            failures.Add(Describe(
                nameof(options.ModelDirectory),
                "an absolute directory path, which the judge requires and cannot share with the embedding model"));
        }

        RequireText(failures, nameof(options.ModelId), options.ModelId);
        RequireText(failures, nameof(options.Revision), options.Revision);
        RequireText(failures, nameof(options.License), options.License);
        RequireArtifactUrl(failures, nameof(options.ModelFileUrl), options.ModelFileUrl);
        RequireArtifactUrl(failures, nameof(options.TokenizerFileUrl), options.TokenizerFileUrl);
        RequireSha256(failures, nameof(options.ModelFileSha256), options.ModelFileSha256);
        RequireSha256(failures, nameof(options.TokenizerFileSha256), options.TokenizerFileSha256);

        if (LocalOnnxModelLayout.IsSha256Hex(options.ModelFileSha256) &&
            string.Equals(options.ModelFileSha256, options.TokenizerFileSha256, StringComparison.Ordinal))
        {
            failures.Add(Describe(
                nameof(options.TokenizerFileSha256),
                "different from ModelFileSha256, because the tokenizer and the model are two different files"));
        }

        RequireRange(failures, nameof(options.MaxTokens), options.MaxTokens, MinTokenWindow, MaxTokenWindow);
        RequireRange(failures, nameof(options.IntraOpThreads), options.IntraOpThreads, 1, MaxIntraOpThreads);
        RequireRange(
            failures,
            nameof(options.InstallTimeoutSeconds),
            options.InstallTimeoutSeconds,
            1,
            MaxInstallTimeoutSeconds);
        RequireRange(failures, nameof(options.MaxDownloadBytes), options.MaxDownloadBytes, 1, MaxDownloadBytesLimit);

        return failures;
    }

    private static void RequireText(List<string> failures, string setting, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add(Describe(setting, "a non-blank value"));
        }
    }

    private static void RequireArtifactUrl(List<string> failures, string setting, string? value)
    {
        if (!LocalOnnxModelLayout.IsHttpsUrl(value) || !LocalOnnxModelLayout.TryGetFileName(value, out _))
        {
            failures.Add(Describe(setting, "an absolute https URL that ends in a file name"));
        }
    }

    private static void RequireSha256(List<string> failures, string setting, string? value)
    {
        if (!LocalOnnxModelLayout.IsSha256Hex(value))
        {
            failures.Add(Describe(setting, "a SHA-256 digest written as 64 lowercase hexadecimal characters"));
        }
    }

    private static void RequireRange(List<string> failures, string setting, long value, long minimum, long maximum)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(Describe(setting, $"between {minimum} and {maximum}"));
        }
    }

    private static string Describe(string setting, string expectation) =>
        $"{LocalOnnxRelevanceJudgeOptions.SectionName}:{setting} must be {expectation}.";
}
