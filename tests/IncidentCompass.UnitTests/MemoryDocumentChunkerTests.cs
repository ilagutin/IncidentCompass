using System.Globalization;
using IncidentCompass.Infrastructure.Embeddings.LocalOnnx;
using IncidentCompass.Infrastructure.Memory;

namespace IncidentCompass.UnitTests;

public sealed class MemoryDocumentChunkerTests
{
    private readonly CharacterEstimateChunkTokenCounter counter = new();

    [Fact]
    public void Chunk_LongDocumentPreservesEverySectionInOrderAndBoundsEveryPassage()
    {
        var options = new MemoryChunkingOptions { MaxTokens = 64, OverlapTokens = 8, MinTokens = 4 };
        var content = "# Runbook\n\n## Diagnose\n" + Lines("diagnosis", 30) +
            "\n## Repair\n" + Lines("remediation", 30) + "\n### Verify\nAll checks passed.";

        var chunks = new MemoryDocumentChunker(counter, options).Chunk("File title", content);

        Assert.True(chunks.Count > 3);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(static chunk => chunk.Position));
        Assert.Equal(["Runbook > Diagnose", "Runbook > Repair", "Runbook > Repair > Verify"],
            chunks.Select(static chunk => chunk.HeadingPath).Distinct());
        Assert.All(chunks, chunk =>
        {
            Assert.StartsWith(chunk.HeadingPath + "\n\n", chunk.Text);
            Assert.InRange(counter.CountTokens(chunk.Text), 1, options.MaxTokens);
        });
        var restored = string.Join('\n', chunks.Select(static chunk => chunk.Text));
        foreach (var label in new[] { "diagnosis", "remediation" })
        {
            var previousIndex = -1;
            for (var index = 0; index < 30; index++)
            {
                var location = restored.IndexOf($"{label} step {index:00}", previousIndex + 1, StringComparison.Ordinal);
                Assert.True(location > previousIndex);
                previousIndex = location;
            }
        }
    }

    [Fact]
    public void Chunk_AdjacentChunksOverlapByTheConfigured48TokensOnWholeLineBoundaries()
    {
        var options = new MemoryChunkingOptions { MaxTokens = 110, OverlapTokens = 48, MinTokens = 8 };
        var lines = Enumerable.Range(0, 8).Select(index => index.ToString("00", CultureInfo.InvariantCulture) + new string('x', 93)).ToArray();
        var chunks = new MemoryDocumentChunker(counter, options).Chunk("R", string.Join('\n', lines));

        Assert.True(chunks.Count > 1);
        for (var index = 1; index < chunks.Count; index++)
        {
            var previous = Body(chunks[index - 1]).Split('\n');
            var current = Body(chunks[index]).Split('\n');
            Assert.Equal(previous[^2..], current[..2]);
            Assert.Equal(48, counter.CountTokens(string.Join('\n', current.Take(2))));
        }
    }

    [Theory]
    [InlineData("# Parent\n## Empty\n### Child\nBody", "Parent > Empty > Child", "Body")]
    [InlineData("Plain text only", "File title", "Plain text only")]
    [InlineData("# Heading ###\n\nBody", "Heading", "Body")]
    public void Chunk_EmptyAncestorsPlainTextAndShortFilesProduceOnePositionZeroChunk(
        string content, string path, string body)
    {
        var chunk = Assert.Single(new MemoryDocumentChunker(counter, new MemoryChunkingOptions()).Chunk("File title", content));

        Assert.Equal(0, chunk.Position);
        Assert.Equal(path, chunk.HeadingPath);
        Assert.Equal(path + "\n\n" + body, chunk.Text);
    }

    [Fact]
    public void Chunk_PrefersParagraphBoundariesAndKeepsHeadingLikeCodeInsideItsSection()
    {
        const string content = "# Runbook\nFirst paragraph.\n\n```sh\n# not a heading\n```\n\n";
        var chunks = new MemoryDocumentChunker(counter, new MemoryChunkingOptions()).Chunk("Title", content);

        Assert.Equal("Runbook", Assert.Single(chunks).HeadingPath);
        Assert.Contains("# not a heading", chunks[0].Text);
    }

    [Fact]
    public void Chunk_MergesAShortTailWithThePreviousFragmentWhenItFits()
    {
        var options = new MemoryChunkingOptions { MaxTokens = 24, OverlapTokens = 0, MinTokens = 8 };
        var chunks = new MemoryDocumentChunker(counter, options).Chunk("R", "A sufficient paragraph of body text.\n\nTail.");

        Assert.Single(chunks);
        Assert.Contains("Tail.", chunks[0].Text);
        Assert.True(counter.CountTokens(chunks[0].Text) <= options.MaxTokens);
    }

    [Fact]
    public void Chunk_RebalancesTheLastParagraphSoTheShortTailStaysInsideTheCap()
    {
        var options = new MemoryChunkingOptions { MaxTokens = 40, OverlapTokens = 0, MinTokens = 12 };
        var first = new string('a', 90);
        var second = new string('b', 60);

        var chunks = new MemoryDocumentChunker(counter, options).Chunk("R", first + "\n\n" + second + "\n\ntail");

        Assert.Equal(2, chunks.Count);
        Assert.Equal(first, Body(chunks[0]));
        Assert.Equal(second + "\n\ntail", Body(chunks[1]));
        Assert.All(chunks, chunk => Assert.InRange(counter.CountTokens(chunk.Text), options.MinTokens, options.MaxTokens));
    }

    [Fact]
    public void Chunk_PreservesCodeIndentationAtTheBeginningAndEndOfAChunk()
    {
        const string body = "    if (checkoutFailed)\n        retry();  ";
        var chunk = Assert.Single(new MemoryDocumentChunker(counter, new MemoryChunkingOptions())
            .Chunk("Runbook", "# Remediation\n" + body));

        Assert.Equal(body, Body(chunk));
    }

    [Fact]
    public void Chunk_RefusesAnOversizedLineInsteadOfTruncatingIt()
    {
        var chunker = new MemoryDocumentChunker(counter, new MemoryChunkingOptions());

        var exception = Assert.Throws<InvalidOperationException>(() => chunker.Chunk("R", new string('z', 3000)));

        Assert.Contains("complete line exceed", exception.Message);
        Assert.DoesNotContain(new string('z', 20), exception.Message);
    }

    [Theory]
    [InlineData(80, 48, 32)]
    [InlineData(79, 48, 32)]
    [InlineData(448, -1, 32)]
    [InlineData(448, 48, 0)]
    [InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public void Validate_RejectsInvalidTokenBudgets(int max, int overlap, int min) =>
        Assert.Throws<InvalidOperationException>(() => new MemoryChunkingOptions
        {
            MaxTokens = max,
            OverlapTokens = overlap,
            MinTokens = min
        }.Validate());

    [Fact]
    public void Validate_DefaultsAndPolicyIdentifyAllParametersAndTheCounter()
    {
        var options = new MemoryChunkingOptions();
        options.Validate();

        Assert.Equal("v1;max=448;overlap=48;min=32;estimate", options.Describe(counter.Kind));
        Assert.Equal("v1;max=448;overlap=48;min=32;exact", options.Describe("exact"));
        LocalOnnxChunkTokenCounter.ValidateWindow(options, 512, 5);
        Assert.Throws<InvalidOperationException>(() => LocalOnnxChunkTokenCounter.ValidateWindow(options, 450, 5));
    }

    private static string Lines(string label, int count) =>
        string.Join('\n', Enumerable.Range(0, count).Select(index => $"{label} step {index:00}"));

    private static string Body(MemoryDocumentChunk chunk) => chunk.Text[(chunk.HeadingPath.Length + 2)..];
}
