namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// What the extended headers between a <c>diff --git</c> line and the <c>---</c> line said, or the
/// refusal they earned.
/// </summary>
/// <param name="CreatesFile">A <c>new file mode</c> header claimed the file is new.</param>
/// <param name="DeletesFile">A <c>deleted file mode</c> header claimed the file is gone.</param>
/// <param name="Code">The refusal, or <c>null</c> when the headers were admitted.</param>
/// <param name="NextIndex">The index of the <c>---</c> line the headers stopped at.</param>
internal sealed record SourcePatchExtendedHeaderScan(
    bool CreatesFile,
    bool DeletesFile,
    string? Code,
    int NextIndex)
{
    public static SourcePatchExtendedHeaderScan Refused(string code) => new(false, false, code, 0);
}
