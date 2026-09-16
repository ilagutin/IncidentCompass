using System.Text.RegularExpressions;

namespace IncidentCompass.Infrastructure.Memory;

/// <summary>Preserves ATX section ancestry and whole lines while bounding each embedding passage.</summary>
internal sealed partial class MemoryDocumentChunker(IMemoryChunkTokenCounter counter, MemoryChunkingOptions options)
{
    public IReadOnlyList<MemoryDocumentChunk> Chunk(string title, string content)
    {
        options.Validate();
        var chunks = new List<MemoryDocumentChunk>();
        foreach (var section in ReadSections(title, content))
        {
            if (section.Lines.Count == 0)
            {
                chunks.Add(HeadingOnlyChunk(section.HeadingPath));
                continue;
            }

            foreach (var text in Split(section))
            {
                chunks.Add(new MemoryDocumentChunk(chunks.Count, section.HeadingPath, text));
            }
        }

        return chunks;
    }

    /// <summary>
    /// A document of headings alone still says what it is about, so it is published as its folded
    /// heading path rather than refused or dropped.
    /// </summary>
    private MemoryDocumentChunk HeadingOnlyChunk(string headingPath)
    {
        if (!Fits(headingPath))
        {
            throw new MemoryDocumentChunkRefusedException(
                "Memory document heading path exceeds the chunk token limit. Shorten the headings.");
        }

        return new MemoryDocumentChunk(0, headingPath, headingPath);
    }

    private IEnumerable<string> Split(MemoryDocumentSection section)
    {
        var lines = section.Lines;
        var start = 0;
        var previousEnd = 0;
        while (start < lines.Count)
        {
            var end = start;
            var paragraphEnd = start;
            while (end < lines.Count && Fits(Render(section, start, end + 1)))
            {
                end++;
                if (string.IsNullOrWhiteSpace(lines[end - 1]))
                {
                    paragraphEnd = end;
                }
            }

            if (end == start)
            {
                throw new MemoryDocumentChunkRefusedException(
                    "Memory section heading and one complete line exceed the chunk token limit. Shorten the line or heading.");
            }

            if (end < lines.Count && paragraphEnd > previousEnd &&
                !string.IsNullOrWhiteSpace(string.Join('\n', lines.Skip(start).Take(paragraphEnd - start))))
            {
                end = paragraphEnd;
            }

            var next = end == lines.Count ? end : FindOverlapStart(section, start, end);
            if (next < lines.Count && counter.CountTokens(Render(section, next, lines.Count)) < options.MinTokens)
            {
                if (Fits(Render(section, start, lines.Count)))
                {
                    end = next = lines.Count;
                }
                else
                {
                    // Move the boundary back to give a small final fragment enough context, without
                    // exceeding the cap or introducing a second copy of an entire chunk.
                    while (end > start + 1 && counter.CountTokens(Render(section, next, lines.Count)) < options.MinTokens)
                    {
                        var earlier = FindOverlapStart(section, start, end - 1);
                        if (!Fits(Render(section, earlier, lines.Count)))
                        {
                            break;
                        }

                        end--;
                        next = earlier;
                    }
                }
            }

            yield return Render(section, start, end);
            previousEnd = end;
            start = next;
            while (start < lines.Count && string.IsNullOrWhiteSpace(lines[start]))
            {
                start++;
            }
        }
    }

    private int FindOverlapStart(MemoryDocumentSection section, int start, int end)
    {
        var next = end;
        while (next > start + 1 &&
            counter.CountOverlapTokens(string.Join('\n', section.Lines.Skip(next - 1).Take(end - next + 1))) <= options.OverlapTokens &&
            Fits(Render(section, next - 1, Math.Min(end + 1, section.Lines.Count))))
        {
            next--;
        }

        return next;
    }

    private bool Fits(string text) => counter.CountTokens(text) <= options.MaxTokens;

    private static string Render(MemoryDocumentSection section, int start, int end) =>
        section.HeadingPath + "\n\n" + string.Join('\n', section.Lines.Skip(start).Take(end - start)).Trim('\n');

    private static IEnumerable<MemoryDocumentSection> ReadSections(string title, string content)
    {
        var headings = new SortedDictionary<int, string>();
        var path = title;
        var emitted = false;
        var lines = new List<string>();
        char? fence = null;
        var fenceLength = 0;
        foreach (var line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var marker = Fence().Match(line);
            if (marker.Success)
            {
                var run = marker.Groups[1].Value;
                if (fence is null)
                {
                    fence = run[0];
                    fenceLength = run.Length;
                }
                else if (run[0] == fence && run.Length >= fenceLength && string.IsNullOrWhiteSpace(marker.Groups[2].Value))
                {
                    fence = null;
                }

                lines.Add(line);
                continue;
            }

            var heading = fence is null ? Heading().Match(line) : Match.Empty;
            if (!heading.Success)
            {
                lines.Add(line);
                continue;
            }

            if (lines.Any(static text => !string.IsNullOrWhiteSpace(text)))
            {
                emitted = true;
                yield return new MemoryDocumentSection(path, lines.ToArray());
            }

            lines.Clear();
            var level = heading.Groups[1].Length;
            foreach (var key in headings.Keys.Where(key => key >= level).ToArray())
            {
                headings.Remove(key);
            }

            var headingTitle = ClosingHashes().Replace(heading.Groups[2].Value, "").Trim();
            if (headingTitle.Length > 0)
            {
                headings[level] = headingTitle;
            }

            path = headings.Count == 0 ? title : string.Join(" > ", headings.Values);
        }

        if (lines.Any(static text => !string.IsNullOrWhiteSpace(text)))
        {
            yield return new MemoryDocumentSection(path, lines.ToArray());
        }
        else if (!emitted)
        {
            // No section has a body: the empty section carries the folded path of every heading.
            yield return new MemoryDocumentSection(path, []);
        }
    }

    [GeneratedRegex(@"^ {0,3}(#{1,6})(?:[ \t]+(.*)|$)")]
    private static partial Regex Heading();

    [GeneratedRegex(@"[ \t]+#+[ \t]*$")]
    private static partial Regex ClosingHashes();

    [GeneratedRegex(@"^ {0,3}(`{3,}|~{3,})(.*)$")]
    private static partial Regex Fence();
}
