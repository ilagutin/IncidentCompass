using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.EmbeddingModels;

/// <summary>
/// Validates the local embedding model settings at start, and only on a host whose embedding
/// provider is <c>LocalOnnx</c>; a host on another provider never reads them.
/// </summary>
internal sealed class LocalOnnxEmbeddingOptionsValidator(IOptions<EmbeddingOptions> embeddingOptions)
    : IValidateOptions<LocalOnnxEmbeddingOptions>
{
    public const int MaxDimensions = 4096;
    public const int MaxTokenWindow = 8192;
    public const int MaxIntraOpThreads = 16;
    public const int MaxInstallTimeoutSeconds = 7200;
    public const int MaxPrefixLength = 64;
    public const long MaxDownloadBytesLimit = 16L << 30;

    public ValidateOptionsResult Validate(string? name, LocalOnnxEmbeddingOptions options)
    {
        if (!ProviderKindParser.IsLocalOnnx(embeddingOptions.Value.Provider))
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = FindFailures(options);
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<string> FindFailures(LocalOnnxEmbeddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ModelDirectory) || !Path.IsPathFullyQualified(options.ModelDirectory))
        {
            failures.Add(Describe(
                nameof(options.ModelDirectory),
                "an absolute directory path, which is required when the embedding provider is LocalOnnx"));
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

        RequireRange(failures, nameof(options.Dimensions), options.Dimensions, 1, MaxDimensions);
        RequireRange(failures, nameof(options.MaxTokens), options.MaxTokens, 3, MaxTokenWindow);
        RequireRange(failures, nameof(options.IntraOpThreads), options.IntraOpThreads, 1, MaxIntraOpThreads);
        RequireRange(
            failures,
            nameof(options.InstallTimeoutSeconds),
            options.InstallTimeoutSeconds,
            1,
            MaxInstallTimeoutSeconds);
        RequireRange(failures, nameof(options.MaxDownloadBytes), options.MaxDownloadBytes, 1, MaxDownloadBytesLimit);
        RequirePrefix(failures, nameof(options.QueryPrefix), options.QueryPrefix);
        RequirePrefix(failures, nameof(options.PassagePrefix), options.PassagePrefix);

        if (!string.Equals(options.Pooling, LocalOnnxEmbeddingOptions.MeanPooling, StringComparison.Ordinal))
        {
            failures.Add(Describe(nameof(options.Pooling), "\"mean\", the only pooling this adapter implements"));
        }

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

    private static void RequirePrefix(List<string> failures, string setting, string? value)
    {
        if (value is null || value.Length > MaxPrefixLength)
        {
            failures.Add(Describe(setting, $"set, and at most {MaxPrefixLength} characters long"));
        }
    }

    private static string Describe(string setting, string expectation) =>
        $"{LocalOnnxEmbeddingOptions.SectionName}:{setting} must be {expectation}.";
}
