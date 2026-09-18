using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The judge settings follow the rules the embedding settings follow, minus every embedding-only
/// one. Each case breaks exactly one setting and asserts that the failure names that setting, so a
/// rule that stops firing is visible rather than absorbed by a neighbouring rule.
/// </summary>
public sealed class LocalOnnxRelevanceJudgeOptionsValidatorTests
{
    // Absolute on either host operating system: Path.IsPathFullyQualified is what the rule asks.
    private static readonly string ModelDirectory =
        Path.Combine(Path.GetTempPath(), "incidentcompass-relevance-judge");

    [Fact]
    public void PinnedDefaults_WithOnlyAModelDirectorySet_AreValid()
    {
        Assert.Empty(LocalOnnxRelevanceJudgeOptionsValidator.FindFailures(Valid()));
    }

    [Fact]
    public void PinnedDefaults_NameTheJudgeItsRevisionAndItsLicense()
    {
        var options = Valid();

        Assert.Equal("BAAI/bge-reranker-v2-m3", options.ModelId);
        Assert.Equal("953dc6f6f85a1b2dbfca4c34a2796e7dde08d41e", options.Revision);
        Assert.Equal("Apache-2.0", options.License);
        Assert.Equal(512, options.MaxTokens);
        Assert.Equal(1800, options.InstallTimeoutSeconds);
        Assert.Equal(1, options.IntraOpThreads);

        // The pinned ONNX file is 570 727 094 bytes, so the default cap has to admit it.
        Assert.True(options.MaxDownloadBytes > 570_727_094);

        // The weights repository and the third-party ONNX export are two repositories at two
        // revisions, and the manifest records each artifact's own URL, so both survive an install.
        Assert.Contains(
            options.ModelId + "/resolve/" + options.Revision + "/",
            options.TokenizerFileUrl,
            StringComparison.Ordinal);
        Assert.DoesNotContain(options.ModelId + "/resolve/", options.ModelFileUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// The judge and the pinned embedding model share one XLM-RoBERTa SentencePiece file byte for
    /// byte, so the two pinned tokenizer digests are deliberately equal. Only the model and tokenizer
    /// digests of one model have to differ.
    /// </summary>
    [Fact]
    public void PinnedTokenizerDigest_IsTheSameFileTheEmbeddingModelPins()
    {
        var options = Valid();

        Assert.Equal(new LocalOnnxEmbeddingOptions().TokenizerFileSha256, options.TokenizerFileSha256);
        Assert.NotEqual(options.ModelFileSha256, options.TokenizerFileSha256);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/judge")]
    public void ModelDirectory_MustBeAnAbsolutePath(string? modelDirectory)
    {
        AssertFails(new LocalOnnxRelevanceJudgeOptions { ModelDirectory = modelDirectory }, "ModelDirectory");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ModelId_MustNotBeBlank(string modelId)
    {
        AssertFails(new LocalOnnxRelevanceJudgeOptions { ModelDirectory = ModelDirectory, ModelId = modelId }, "ModelId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Revision_MustNotBeBlank(string revision)
    {
        AssertFails(
            new LocalOnnxRelevanceJudgeOptions { ModelDirectory = ModelDirectory, Revision = revision },
            "Revision");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void License_MustNotBeBlank(string license)
    {
        AssertFails(new LocalOnnxRelevanceJudgeOptions { ModelDirectory = ModelDirectory, License = license }, "License");
    }

    [Theory]
    [InlineData("http://models.example/judge/model.onnx")]
    [InlineData("ftp://models.example/judge/model.onnx")]
    [InlineData("models.example/judge/model.onnx")]
    [InlineData("https://models.example/judge/")]
    [InlineData("")]
    public void ModelFileUrl_MustBeAnHttpsUrlEndingInAFileName(string modelFileUrl)
    {
        AssertFails(
            new LocalOnnxRelevanceJudgeOptions { ModelDirectory = ModelDirectory, ModelFileUrl = modelFileUrl },
            "ModelFileUrl");
    }

    [Theory]
    [InlineData("http://models.example/judge/sentencepiece.bpe.model")]
    [InlineData("https://models.example/judge/")]
    public void TokenizerFileUrl_MustBeAnHttpsUrlEndingInAFileName(string tokenizerFileUrl)
    {
        AssertFails(
            new LocalOnnxRelevanceJudgeOptions { ModelDirectory = ModelDirectory, TokenizerFileUrl = tokenizerFileUrl },
            "TokenizerFileUrl");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-digest")]
    [InlineData("912FC1215C2DBFF6499700534BD8D31253AF01573861ABBFC43AFD1FAB6CCE5D")]
    [InlineData("912fc1215c2dbff6499700534bd8d31253af01573861abbfc43afd1fab6cce5")]
    public void ModelFileSha256_MustBeSixtyFourLowercaseHexCharacters(string modelFileSha256)
    {
        AssertFails(
            new LocalOnnxRelevanceJudgeOptions { ModelDirectory = ModelDirectory, ModelFileSha256 = modelFileSha256 },
            "ModelFileSha256");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-digest")]
    public void TokenizerFileSha256_MustBeSixtyFourLowercaseHexCharacters(string tokenizerFileSha256)
    {
        AssertFails(
            new LocalOnnxRelevanceJudgeOptions
            {
                ModelDirectory = ModelDirectory,
                TokenizerFileSha256 = tokenizerFileSha256
            },
            "TokenizerFileSha256");
    }

    [Fact]
    public void TokenizerFileSha256_MustNotBeTheModelFileDigest()
    {
        var options = new LocalOnnxRelevanceJudgeOptions { ModelDirectory = ModelDirectory };

        AssertFails(
            new LocalOnnxRelevanceJudgeOptions
            {
                ModelDirectory = ModelDirectory,
                TokenizerFileSha256 = options.ModelFileSha256
            },
            "TokenizerFileSha256");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(LocalOnnxPairEncoder.SpecialTokenCount)]
    [InlineData(LocalOnnxRelevanceJudgeOptionsValidator.MaxTokenWindow + 1)]
    public void MaxTokens_MustLeaveRoomForContentAndStayInTheWindow(int maxTokens)
    {
        AssertFails(
            new LocalOnnxRelevanceJudgeOptions { ModelDirectory = ModelDirectory, MaxTokens = maxTokens },
            "MaxTokens");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(LocalOnnxRelevanceJudgeOptionsValidator.MaxIntraOpThreads + 1)]
    public void IntraOpThreads_MustBeBetweenOneAndTheThreadLimit(int intraOpThreads)
    {
        AssertFails(
            new LocalOnnxRelevanceJudgeOptions { ModelDirectory = ModelDirectory, IntraOpThreads = intraOpThreads },
            "IntraOpThreads");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(LocalOnnxRelevanceJudgeOptionsValidator.MaxInstallTimeoutSeconds + 1)]
    public void InstallTimeoutSeconds_MustBeBetweenOneSecondAndTheTimeoutLimit(int installTimeoutSeconds)
    {
        AssertFails(
            new LocalOnnxRelevanceJudgeOptions
            {
                ModelDirectory = ModelDirectory,
                InstallTimeoutSeconds = installTimeoutSeconds
            },
            "InstallTimeoutSeconds");
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(LocalOnnxRelevanceJudgeOptionsValidator.MaxDownloadBytesLimit + 1)]
    public void MaxDownloadBytes_MustBeBetweenOneByteAndTheDownloadLimit(long maxDownloadBytes)
    {
        AssertFails(
            new LocalOnnxRelevanceJudgeOptions { ModelDirectory = ModelDirectory, MaxDownloadBytes = maxDownloadBytes },
            "MaxDownloadBytes");
    }

    /// <summary>
    /// The settings become the store's kind-neutral pin, of the judge kind and with no embedding
    /// profile: a cross-encoder has no vector width, no pooling and no prefixes.
    /// </summary>
    [Fact]
    public void CreatePin_ProducesARelevanceJudgePinWithNoEmbeddingProfile()
    {
        var options = Valid();

        var pin = options.CreatePin();

        Assert.Equal(LocalOnnxModelManifest.RelevanceJudgeKind, pin.Kind);
        Assert.Null(pin.EmbeddingProfile);
        Assert.Equal(options.ModelId, pin.ModelId);
        Assert.Equal(options.MaxTokens, pin.MaxTokens);
        Assert.Equal(LocalOnnxModelArtifact.OnnxKind, pin.ModelFile.Kind);
        Assert.Equal(LocalOnnxModelArtifact.SentencePieceKind, pin.TokenizerFile.Kind);
        Assert.Equal(options.ModelFileUrl, pin.ModelFile.Url);
        Assert.Equal(options.TokenizerFileUrl, pin.TokenizerFile.Url);
    }

    private static LocalOnnxRelevanceJudgeOptions Valid() => new() { ModelDirectory = ModelDirectory };

    private static void AssertFails(LocalOnnxRelevanceJudgeOptions options, string setting)
    {
        var failures = LocalOnnxRelevanceJudgeOptionsValidator.FindFailures(options);

        var expected = LocalOnnxRelevanceJudgeOptions.SectionName + ":" + setting + " must be";
        Assert.Contains(failures, failure => failure.StartsWith(expected, StringComparison.Ordinal));
    }
}
