using System.Security.Cryptography;
using System.Text;
using IncidentCompass.Infrastructure.Postgres;

namespace IncidentCompass.UnitTests;

public sealed class PostgresMigrationChecksumPolicyTests
{
    [Theory]
    [InlineData("first\nsecond\nthird\n")]
    [InlineData("first\r\nsecond\r\nthird\r\n")]
    [InlineData("first\rsecond\rthird\r")]
    [InlineData("first\r\nsecond\rthird\n")]
    [InlineData("\uFEFFfirst\r\nsecond\nthird\r")]
    public void CanonicalChecksumNormalizesDecodedBomAndEveryLineEnding(string sql)
    {
        var policy = CreatePolicy(sql);

        Assert.Equal(
            ComputeExpected("001-example.sql", "first\nsecond\nthird\n"),
            policy.CanonicalChecksum.Value);
    }

    [Fact]
    public void CanonicalChecksumPreservesMeaningfulSqlAndScriptNameChanges()
    {
        var baseline = CreatePolicy("SELECT 1;\n").CanonicalChecksum;
        var changedSql = CreatePolicy("SELECT 2;\n").CanonicalChecksum;
        var changedName = PostgresMigrationChecksumPolicy.Create(
            1,
            "example",
            [new("002-example.sql", "SELECT 1;\n")],
            acceptsReleasedLegacyChecksums: false).CanonicalChecksum;

        Assert.NotEqual(baseline, changedSql);
        Assert.NotEqual(baseline, changedName);
    }

    [Fact]
    public void ReleasedLegacyChecksumRequiresExactVersionNameAndCrlfHash()
    {
        var policy = PostgresMigrationChecksumPolicy.Create(
            17,
            "v0.3-example",
            [new("026-example.sql", "first\nsecond\n")],
            acceptsReleasedLegacyChecksums: true);
        var legacyCrlf = ComputeExpected("026-example.sql", "first\r\nsecond\r\n");

        Assert.True(policy.Accepts(17, "v0.3-example", legacyCrlf));
        Assert.False(policy.Accepts(18, "v0.3-example", legacyCrlf));
        Assert.False(policy.Accepts(17, "renamed", legacyCrlf));
        Assert.False(policy.Accepts(17, "v0.3-example", new string('0', 64)));
    }

    [Fact]
    public void NewCatalogEntryDoesNotAcceptLegacyCrlfChecksumByDefault()
    {
        var policy = PostgresMigrationChecksumPolicy.Create(
            18,
            "v0.4-example",
            [new("027-example.sql", "first\nsecond\n")],
            acceptsReleasedLegacyChecksums: false);
        var legacyCrlf = ComputeExpected("027-example.sql", "first\r\nsecond\r\n");

        Assert.False(policy.Accepts(18, "v0.4-example", legacyCrlf));
        Assert.True(policy.Accepts(
            18,
            "v0.4-example",
            policy.CanonicalChecksum.Value));
    }

    private static PostgresMigrationChecksumPolicy CreatePolicy(string sql) =>
        PostgresMigrationChecksumPolicy.Create(
            1,
            "example",
            [new("001-example.sql", sql)],
            acceptsReleasedLegacyChecksums: false);

    private static string ComputeExpected(string scriptName, string sql)
    {
        var bytes = Encoding.UTF8.GetBytes(scriptName + "\0" + sql + "\0");
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
