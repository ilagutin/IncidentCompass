using System.Text;
using IncidentCompass.Infrastructure.SourceContext;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Proves the bridge to a remote repository is git's own naming and not something merely similar.
/// </summary>
/// <remarks>
/// The expected values are not this code's own output recorded as a baseline. They are what
/// <c>git hash-object</c> produces for the same bytes, so a change that broke the format - a missing
/// NUL, a length in the wrong units, a hash over the content alone - fails here rather than silently
/// producing a correspondence that never matches anything a provider reports.
/// </remarks>
public sealed class GitBlobIdentityTests
{
    [Theory]
    [InlineData("", "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391")]
    [InlineData("hello\n", "ce013625030ba8dba906f756967f9e9ca394464a")]
    [InlineData("what is up, doc?", "bd9dbf5aae1a3862dd1526723246b20206e5fc37")]
    [InlineData("namespace A;\n", "6a92c53729e4930e7febd4ac6a6930acb7445c51")]
    public void ComputeMatchesGitObjectNaming(string content, string expected) =>
        Assert.Equal(expected, GitBlobIdentity.Compute(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task StreamingComputeAgreesWithTheBufferedOne()
    {
        var content = Encoding.UTF8.GetBytes(new string('x', 200_000) + "\n");
        using var stream = new MemoryStream(content);

        var streamed = await GitBlobIdentity.ComputeAsync(
            stream, content.Length, new byte[4096], TestContext.Current.CancellationToken);

        Assert.Equal(GitBlobIdentity.Compute(content), streamed);
    }

    [Fact]
    public async Task StreamingComputeRefusesAFileThatDidNotHoldWhatItPromised()
    {
        using var stream = new MemoryStream("short"u8.ToArray());

        await Assert.ThrowsAsync<IOException>(() => GitBlobIdentity.ComputeAsync(
            stream, 4096, new byte[64], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("E69DE29BB2D1D6434B8B29AE775AD8C2E48C5391", false)]
    [InlineData("e69de29bb2d1d6434b8b29ae775ad8c2e48c539", false)]
    [InlineData("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", true)]
    public void OnlyLowerHexObjectNamesAreValid(string? value, bool expected) =>
        Assert.Equal(expected, GitBlobIdentity.IsValid(value));
}
