using System.Text;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The event-stream framing must not depend on where the network splits a body, so every case is
/// checked delivered whole, one byte per read, and split at every possible point into two reads.
/// </summary>
public sealed class OpenAiServerSentEventParserTests
{
    public static TheoryData<string, string[]> Bodies => new()
    {
        { "data: one\n\ndata: two\n\n", ["one", "two"] },
        { "data: one\r\n\r\ndata: two\r\n\r\n", ["one", "two"] },
        { "data: one\r\rdata: two\r\r", ["one", "two"] },
        { "data: first line\r\ndata: second line\n\n", ["first line\nsecond line"] },
        { "\uFEFFdata: after bom\n\n", ["after bom"] },
        { ": keep-alive\n\n: another\r\n\r\n", [] },
        { "event: delta\nid: 7\nretry: 1000\nunknown: x\ndata: kept\n\n", ["kept"] },
        { "data:no space\n\ndata:  two spaces\n\n", ["no space", " two spaces"] },
        { "data\n\ndata:\n\ndata: \n\n", [] },
        { "data: ü€𝄞\n\n", ["ü€𝄞"] },
        { "data: [DONE]\n\n", ["[DONE]"] },
        { "data: complete\n\ndata: never finished\n", ["complete"] },
        { "data: never ended", [] },
        { "\n\n\n\ndata: x\n\n", ["x"] }
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Append_DeliveredWhole_DispatchesTheCompletedEvents(string body, string[] expected)
    {
        Assert.Equal(expected, Parse(Encoding.UTF8.GetBytes(body), int.MaxValue));
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Append_DeliveredOneBytePerRead_DispatchesTheSameEvents(string body, string[] expected)
    {
        Assert.Equal(expected, Parse(Encoding.UTF8.GetBytes(body), 1));
    }

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Append_SplitAtEveryPoint_DispatchesTheSameEvents(string body, string[] expected)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var parser = new OpenAiServerSentEventParser();
            var dispatched = new List<string>();
            parser.Append(bytes.AsSpan(0, split), dispatched);
            parser.Append(bytes.AsSpan(split), dispatched);
            Assert.Equal(expected, dispatched);
        }
    }

    [Fact]
    public void Append_ByteOrderMarkAfterTheStart_IsKeptAsData()
    {
        var dispatched = Parse(Encoding.UTF8.GetBytes("data: a\n\ndata: \uFEFFb\n\n"), int.MaxValue);

        Assert.Equal(["a", "\uFEFFb"], dispatched);
    }

    [Fact]
    public void Append_CarriageReturnEndingOneReadAndLineFeedStartingTheNext_IsOneLineBreak()
    {
        var parser = new OpenAiServerSentEventParser();
        var dispatched = new List<string>();

        parser.Append("data: a\r"u8, dispatched);
        parser.Append("\ndata: b\r"u8, dispatched);
        Assert.Empty(dispatched);
        parser.Append("\n\r\n"u8, dispatched);

        Assert.Equal(["a\nb"], dispatched);
    }

    [Fact]
    public void Append_InvalidUtf8_IsReplacedRatherThanThrown()
    {
        var dispatched = Parse([.. "data: "u8, 0xFF, 0xFE, .. "\n\n"u8], int.MaxValue);

        Assert.Equal(["\uFFFD\uFFFD"], dispatched);
    }

    [Theory]
    [InlineData("data: 0123456789")]
    [InlineData(": a comment that never ends and never is an event")]
    [InlineData("event: an ignored field can still not grow without bound")]
    public void Append_LineLongerThanTheCap_IsRefusedBeforeItEnds(string line)
    {
        var parser = new OpenAiServerSentEventParser(maxEventCharacters: 12);

        Assert.Throws<OpenAiResponseBodyTooLargeException>(() =>
            parser.Append(Encoding.UTF8.GetBytes(line), []));
    }

    [Fact]
    public void Append_EventWhoseJoinedDataExceedsTheCap_IsRefused()
    {
        var parser = new OpenAiServerSentEventParser(maxEventCharacters: 12);
        var dispatched = new List<string>();

        parser.Append("data: 12345\ndata: 12345\n"u8, dispatched);

        Assert.Throws<OpenAiResponseBodyTooLargeException>(() => parser.Append("data: 12345\n"u8, dispatched));
        Assert.Empty(dispatched);
    }

    [Fact]
    public void Append_EventsEachWithinTheCap_AreNotLimitedByTheirTotal()
    {
        var parser = new OpenAiServerSentEventParser(maxEventCharacters: 12);
        var dispatched = new List<string>();

        for (var index = 0; index < 100; index++)
        {
            parser.Append("data: 12345\n\n"u8, dispatched);
        }

        Assert.Equal(100, dispatched.Count);
    }

    private static List<string> Parse(byte[] bytes, int readSize)
    {
        var parser = new OpenAiServerSentEventParser();
        var dispatched = new List<string>();
        var size = Math.Min(readSize, Math.Max(1, bytes.Length));
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            parser.Append(bytes.AsSpan(offset, Math.Min(size, bytes.Length - offset)), dispatched);
        }

        return dispatched;
    }
}
