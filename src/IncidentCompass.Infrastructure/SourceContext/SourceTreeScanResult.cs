namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// The outcome of scanning a tree for its identity: the admitted entries, or a refusal code.
/// </summary>
internal sealed record SourceTreeScanResult(IReadOnlyList<SourceTreeEntry>? Entries, string Code)
{
    public static SourceTreeScanResult Scanned(IReadOnlyList<SourceTreeEntry> entries) =>
        new(entries, SourceWorkspaceCodes.Created);

    public static SourceTreeScanResult Refused(string code) => new(null, code);
}
