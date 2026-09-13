using System.Xml.Linq;
using IncidentCompass.Infrastructure.EmbeddingModels;
using IncidentCompass.TestSupport;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The license of every model artifact this repository points at is carried by code and checked by
/// this test, not remembered: the shipped model defaults and the committed fixture manifest must
/// name a license on the allow-list, and the third-party notices must name the default model, its
/// revision and the embedding packages at the versions the solution pins, so none of them can drift
/// apart unnoticed.
/// </summary>
public sealed class ThirdPartyLicenseTests
{
    private static readonly string[] AllowedLicenses = ["MIT", "Apache-2.0"];

    private static readonly string[] EmbeddingPackages = ["Microsoft.ML.OnnxRuntime", "Microsoft.ML.Tokenizers"];

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

        foreach (var package in EmbeddingPackages)
        {
            Assert.True(pinnedVersions.TryGetValue(package, out var version), $"{package} is not pinned in Directory.Packages.props.");
            var row = notices
                .Split('\n')
                .SingleOrDefault(line => line.StartsWith("| " + package + " |", StringComparison.Ordinal));
            Assert.True(row is not null, $"THIRD-PARTY-NOTICES.md has no table row for {package}.");
            var cells = row.Split('|', StringSplitOptions.TrimEntries);
            Assert.Equal(version, cells[2]);
            Assert.Contains(cells[3], AllowedLicenses);
        }
    }

    private static string ReadNotices() =>
        File.ReadAllText(Path.Combine(RepositoryRootLocator.Find(), "THIRD-PARTY-NOTICES.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
}
