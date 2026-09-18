using System.Xml.Linq;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.Infrastructure.Relevance.LocalOnnx;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The license of every model artifact this repository points at is carried by code and checked by
/// this test, not remembered: the shipped defaults of both models and both committed fixture
/// manifests must name a license on the allow-list, and the third-party notices must name each model,
/// its revision and the model packages at the versions the solution pins, so none of them can drift
/// apart unnoticed.
/// <para>
/// The judge is the case the wording has to be careful about. Its weights are Apache-2.0, but the
/// ONNX file the product downloads comes from a third-party export repository that declares no
/// license of its own, so the notices have to name both repositories, both revisions and that fact.
/// A test that only checked the declared license would pass while the notices claimed something
/// neither repository states, so the sentence itself is asserted.
/// </para>
/// </summary>
public sealed class ThirdPartyLicenseTests
{
    private static readonly string[] AllowedLicenses = ["MIT", "Apache-2.0"];

    private static readonly string[] ModelPackages = ["Microsoft.ML.OnnxRuntime", "Microsoft.ML.Tokenizers"];

    [Fact]
    public void ShippedLocalModelDefaults_NameAnAllowedLicense()
    {
        Assert.Contains(new LocalOnnxEmbeddingOptions().License, AllowedLicenses);
    }

    [Fact]
    public async Task CommittedFixtureManifest_NamesAnAllowedLicense()
    {
        var manifest = await LocalOnnxFixtureModel.ReadFixtureManifestAsync(TestContext.Current.CancellationToken);

        Assert.Contains(manifest.License, AllowedLicenses);
    }

    [Fact]
    public void ThirdPartyNotices_NameTheDefaultModelItsRevisionLicenseAndFiles()
    {
        var notices = ReadNotices();
        var defaults = new LocalOnnxEmbeddingOptions();

        Assert.Contains(defaults.ModelId, notices, StringComparison.Ordinal);
        Assert.Contains(defaults.Revision, notices, StringComparison.Ordinal);
        Assert.Contains(defaults.License + " License", notices, StringComparison.Ordinal);
        Assert.Contains(defaults.ModelFileSha256, notices, StringComparison.Ordinal);
        Assert.Contains(defaults.TokenizerFileSha256, notices, StringComparison.Ordinal);
        Assert.True(LocalOnnxModelLayout.TryGetFileName(defaults.ModelFileUrl, out var modelFileName));
        Assert.True(LocalOnnxModelLayout.TryGetFileName(defaults.TokenizerFileUrl, out var tokenizerFileName));
        Assert.Contains(modelFileName, notices, StringComparison.Ordinal);
        Assert.Contains(tokenizerFileName, notices, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedRelevanceJudgeDefaults_NameAnAllowedLicense()
    {
        Assert.Contains(new LocalOnnxRelevanceJudgeOptions().License, AllowedLicenses);
    }

    [Fact]
    public async Task CommittedRelevanceJudgeFixtureManifest_NamesAnAllowedLicense()
    {
        var manifest = await LocalOnnxRelevanceJudgeFixtureModel.ReadFixtureManifestAsync(
            TestContext.Current.CancellationToken);

        Assert.Contains(manifest.License, AllowedLicenses);
    }

    [Fact]
    public void ThirdPartyNotices_NameTheDefaultRelevanceJudgeItsRevisionLicenseAndFiles()
    {
        var notices = ReadNotices();
        var defaults = new LocalOnnxRelevanceJudgeOptions();

        Assert.Contains(defaults.ModelId, notices, StringComparison.Ordinal);
        Assert.Contains(defaults.Revision, notices, StringComparison.Ordinal);
        Assert.Contains(defaults.License + " License", notices, StringComparison.Ordinal);
        Assert.Contains(defaults.ModelFileSha256, notices, StringComparison.Ordinal);
        Assert.Contains(defaults.TokenizerFileSha256, notices, StringComparison.Ordinal);
        Assert.True(LocalOnnxModelLayout.TryGetFileName(defaults.ModelFileUrl, out var modelFileName));
        Assert.True(LocalOnnxModelLayout.TryGetFileName(defaults.TokenizerFileUrl, out var tokenizerFileName));
        Assert.Contains(modelFileName, notices, StringComparison.Ordinal);
        Assert.Contains(tokenizerFileName, notices, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ONNX file is fetched from a repository that is not the one the license belongs to, so the
    /// notices must carry both URLs in full, which is what names both repositories and both revisions,
    /// and must say that the export declares nothing of its own rather than implying it is licensed.
    /// </summary>
    [Fact]
    public void ThirdPartyNotices_SayTheRelevanceJudgeOnnxExportDeclaresNoLicenseOfItsOwn()
    {
        var notices = ReadNotices();
        var defaults = new LocalOnnxRelevanceJudgeOptions();

        Assert.Contains(defaults.ModelFileUrl, notices, StringComparison.Ordinal);
        Assert.Contains(defaults.TokenizerFileUrl, notices, StringComparison.Ordinal);
        Assert.Contains("onnx-community/bge-reranker-v2-m3-ONNX", notices, StringComparison.Ordinal);
        Assert.Contains("declares no license of its own", notices, StringComparison.Ordinal);
    }

    /// <summary>
    /// The judge pins the embedding model's tokenizer digest on purpose, because the two models share
    /// that file byte for byte. The notices have to record it as the deliberate choice it is, or the
    /// next reader treats a repeated digest as a mistake and "fixes" it.
    /// </summary>
    [Fact]
    public void ThirdPartyNotices_RecordThatBothModelsPinTheSameTokenizerFile()
    {
        var notices = ReadNotices();

        Assert.Equal(
            new LocalOnnxEmbeddingOptions().TokenizerFileSha256,
            new LocalOnnxRelevanceJudgeOptions().TokenizerFileSha256);
        Assert.Contains("byte for byte", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void ThirdPartyNotices_NameTheEmbeddingPackagesAtThePinnedVersionsAndAnAllowedLicense()
    {
        var notices = ReadNotices();
        var pinnedVersions = XDocument
            .Load(Path.Combine(RepositoryRootLocator.Find(), "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .ToDictionary(
                static element => (string)element.Attribute("Include")!,
                static element => (string)element.Attribute("Version")!,
                StringComparer.Ordinal);

        foreach (var package in ModelPackages)
        {
            Assert.True(pinnedVersions.TryGetValue(package, out var version), $"{package} is not pinned in Directory.Packages.props.");
            var row = FindPackageRow(notices, package);
            var cells = row.Split('|', StringSplitOptions.TrimEntries);
            Assert.Equal(version, cells[2]);
            Assert.Contains(cells[3], AllowedLicenses);
        }
    }

    /// <summary>
    /// The judge brings no package of its own; it runs on the two the embedding model already brings.
    /// Their rows have to say so, so that removing the embedding model would not quietly drop the
    /// notice for packages the judge still needs.
    /// </summary>
    [Fact]
    public void ThirdPartyNotices_SayTheModelPackagesAlsoServeTheRelevanceJudge()
    {
        var notices = ReadNotices();

        foreach (var package in ModelPackages)
        {
            Assert.Contains(
                "relevance judge",
                FindPackageRow(notices, package),
                StringComparison.Ordinal);
        }
    }

    private static string FindPackageRow(string notices, string package)
    {
        var row = notices
            .Split('\n')
            .SingleOrDefault(line => line.StartsWith("| " + package + " |", StringComparison.Ordinal));
        Assert.True(row is not null, $"THIRD-PARTY-NOTICES.md has no table row for {package}.");
        return row;
    }

    private static string ReadNotices() =>
        File.ReadAllText(Path.Combine(RepositoryRootLocator.Find(), "THIRD-PARTY-NOTICES.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
}
