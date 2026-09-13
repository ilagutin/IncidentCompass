using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The shipped defaults are the pinned multilingual E5 artifacts, and every setting the store and the
/// adapter depend on is refused at start when it is out of shape, but only on a LocalOnnx host.
/// </summary>
public sealed class LocalOnnxEmbeddingOptionsTests
{
    private static readonly string ModelDirectory = Path.Combine(Path.GetTempPath(), "incidentcompass-models");

    [Fact]
    public void Defaults_AreThePinnedMultilingualE5SmallArtifacts()
    {
        var options = new LocalOnnxEmbeddingOptions();

        Assert.Equal("intfloat/multilingual-e5-small", options.ModelId);
        Assert.Equal("614241f622f53c4eeff9890bdc4f31cfecc418b3", options.Revision);
        Assert.Equal(
            "https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3/onnx/model_qint8_avx512_vnni.onnx",
            options.ModelFileUrl);
        Assert.Equal("dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88", options.ModelFileSha256);
        Assert.Equal(
            "https://huggingface.co/intfloat/multilingual-e5-small/resolve/614241f622f53c4eeff9890bdc4f31cfecc418b3/onnx/sentencepiece.bpe.model",
            options.TokenizerFileUrl);
        Assert.Equal("cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865", options.TokenizerFileSha256);
        Assert.Equal(384, options.Dimensions);
        Assert.Equal(512, options.MaxTokens);
        Assert.Equal("query: ", options.QueryPrefix);
        Assert.Equal("passage: ", options.PassagePrefix);
        Assert.Equal("mean", options.Pooling);
        Assert.True(options.Normalize);
        Assert.Equal("MIT", options.License);
        Assert.Equal(1, options.IntraOpThreads);
        Assert.Equal(1L << 30, options.MaxDownloadBytes);
        Assert.Null(options.ModelDirectory);
    }

    [Fact]
    public void Defaults_WithAModelDirectory_AreValid()
    {
        Assert.Empty(LocalOnnxEmbeddingOptionsValidator.FindFailures(new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory }));
    }

    [Theory]
    [InlineData("missing-directory", "ModelDirectory")]
    [InlineData("relative-directory", "ModelDirectory")]
    [InlineData("blank-model-id", "ModelId")]
    [InlineData("blank-revision", "Revision")]
    [InlineData("http-model-url", "ModelFileUrl")]
    [InlineData("model-url-without-file", "ModelFileUrl")]
    [InlineData("uppercase-model-digest", "ModelFileSha256")]
    [InlineData("short-tokenizer-digest", "TokenizerFileSha256")]
    [InlineData("same-digest", "TokenizerFileSha256")]
    [InlineData("zero-download-limit", "MaxDownloadBytes")]
    [InlineData("download-limit-too-large", "MaxDownloadBytes")]
    [InlineData("zero-dimensions", "Dimensions")]
    [InlineData("window-too-small", "MaxTokens")]
    [InlineData("too-many-threads", "IntraOpThreads")]
    [InlineData("zero-timeout", "InstallTimeoutSeconds")]
    [InlineData("long-query-prefix", "QueryPrefix")]
    [InlineData("cls-pooling", "Pooling")]
    [InlineData("blank-license", "License")]
    public void FindFailures_NamesTheSettingThatIsOutOfShape(string invalidCase, string setting)
    {
        var failures = LocalOnnxEmbeddingOptionsValidator.FindFailures(Invalid(invalidCase));

        Assert.Contains(failures, failure =>
            failure.StartsWith(LocalOnnxEmbeddingOptions.SectionName + ":" + setting + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_OnAnotherEmbeddingProvider_SkipsTheLocalSettings()
    {
        var validator = new LocalOnnxEmbeddingOptionsValidator(Options.Create(new EmbeddingOptions { Provider = "Mock" }));

        var result = validator.Validate(null, new LocalOnnxEmbeddingOptions());

        Assert.True(result.Skipped);
    }

    [Fact]
    public void Validator_OnALocalOnnxHost_RefusesMissingModelDirectory()
    {
        var validator = new LocalOnnxEmbeddingOptionsValidator(Options.Create(new EmbeddingOptions { Provider = "LocalOnnx" }));

        var result = validator.Validate(null, new LocalOnnxEmbeddingOptions());

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("ModelDirectory", StringComparison.Ordinal));
    }

    private static LocalOnnxEmbeddingOptions Invalid(string invalidCase) => invalidCase switch
    {
        "missing-directory" => new LocalOnnxEmbeddingOptions(),
        "relative-directory" => new LocalOnnxEmbeddingOptions { ModelDirectory = "models" },
        "blank-model-id" => new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory, ModelId = " " },
        "blank-revision" => new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory, Revision = "" },
        "http-model-url" => new LocalOnnxEmbeddingOptions
        {
            ModelDirectory = ModelDirectory,
            ModelFileUrl = "http://huggingface.co/intfloat/multilingual-e5-small/resolve/main/onnx/model.onnx"
        },
        "model-url-without-file" => new LocalOnnxEmbeddingOptions
        {
            ModelDirectory = ModelDirectory,
            ModelFileUrl = "https://huggingface.co/"
        },
        "uppercase-model-digest" => new LocalOnnxEmbeddingOptions
        {
            ModelDirectory = ModelDirectory,
            ModelFileSha256 = "DD476DD0C2514E9B9BE83AEB3853FAC0763E0BDF4A71645407587D77C48A2D88"
        },
        "short-tokenizer-digest" => new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory, TokenizerFileSha256 = "cfc8146a" },
        "same-digest" => new LocalOnnxEmbeddingOptions
        {
            ModelDirectory = ModelDirectory,
            TokenizerFileSha256 = "dd476dd0c2514e9b9be83aeb3853fac0763e0bdf4a71645407587d77c48a2d88"
        },
        "zero-download-limit" => new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory, MaxDownloadBytes = 0 },
        "download-limit-too-large" => new LocalOnnxEmbeddingOptions
        {
            ModelDirectory = ModelDirectory,
            MaxDownloadBytes = LocalOnnxEmbeddingOptionsValidator.MaxDownloadBytesLimit + 1
        },
        "zero-dimensions" => new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory, Dimensions = 0 },
        "window-too-small" => new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory, MaxTokens = 2 },
        "too-many-threads" => new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory, IntraOpThreads = 17 },
        "zero-timeout" => new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory, InstallTimeoutSeconds = 0 },
        "long-query-prefix" => new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory, QueryPrefix = new string('q', 65) },
        "cls-pooling" => new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory, Pooling = "cls" },
        "blank-license" => new LocalOnnxEmbeddingOptions { ModelDirectory = ModelDirectory, License = " " },
        _ => throw new ArgumentOutOfRangeException(nameof(invalidCase), invalidCase, null)
    };
}
