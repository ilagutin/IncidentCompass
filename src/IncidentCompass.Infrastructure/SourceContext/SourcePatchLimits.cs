using IncidentCompass.Application.Governance.ActionApprovals;

namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// What one patch may be and may touch. Every bound is a refusal of the whole patch, never a partial
/// acceptance, for the same reason materialization refuses rather than truncates: a half-applied
/// change would carry a tree identity for a tree nobody asked for.
/// </summary>
/// <param name="MaximumPatchBytes">
/// Raw UTF-8 bytes of the patch text. See <see cref="RawBudgetBytes"/> for where the number comes
/// from.
/// </param>
/// <param name="MaximumFiles">File sections one patch may carry.</param>
/// <param name="MaximumHunksPerFile">Hunks one file section may carry.</param>
/// <param name="MaximumPathCharacters">Characters in one repository-relative path.</param>
/// <param name="MaximumPathSegments">
/// Segments in one repository-relative path, which is the depth of the file it names. It is the
/// depth bound the tree walk enforces (<see cref="SourceWorkspaceBounds.MaxDepth"/>), applied to the
/// text so that a patch creating a file deeper than the workspace admits is refused from the diff
/// rather than written and then undone by a walk that cannot identify the result.
/// </param>
/// <param name="MaximumTargetBytes">
/// Bytes of a file the patch touches, checked against the file before the change and against the
/// result after it. It is the same bound the excerpt reader applies
/// (<see cref="SourceContextOptions.MaxSourceBytes"/>), read from the same options rather than
/// restated here, so a patch cannot leave behind a file the evidence path would then refuse to
/// quote. It also bounds the base line a hunk header may name, since a file of <c>n</c> bytes holds
/// at most <c>n</c> lines.
/// </param>
/// <param name="AllowedExtensions">
/// Extensions a touched path may carry, matched case-insensitively, read from the same options the
/// excerpt reader uses. A patch that created or changed a file outside this set would produce a
/// change nothing downstream could quote back as evidence, so the change is refused rather than made
/// unquotable.
/// </param>
/// <remarks>
/// <b>What one attempt costs in memory.</b> Far more than the bytes it writes, and the multiple is
/// what the bounds have to be sized for. An all-or-nothing apply holds every file's original bytes
/// until the last write succeeds, and each file is also decoded to a UTF-16 string at two bytes per
/// character, split into a line array holding a second copy of those characters as separate objects,
/// rebuilt as a result list, joined into a result string and encoded into result bytes, with all of
/// it live at once. Measured on .NET 10 for the worst case these bounds admit, sixteen files of
/// 250 KiB: about 46 MiB allocated when the files hold ordinary 35-character source lines, and about
/// 144 MiB when they hold one-character lines, where the per-object overhead of 127,000 line strings
/// per file dominates everything else. That is twelve to thirty-seven times the 4 MiB of content,
/// not one times it.
/// </remarks>
internal sealed record SourcePatchLimits(
    int MaximumPatchBytes,
    int MaximumFiles,
    int MaximumHunksPerFile,
    int MaximumPathCharacters,
    int MaximumPathSegments,
    int MaximumTargetBytes,
    IReadOnlyCollection<string> AllowedExtensions)
{
    /// <summary>
    /// Bytes held back from the action-payload ceiling for the fields that travel beside the patch:
    /// two 64-character tree identities and their keys, the JSON punctuation around them, and room
    /// for the envelope to gain a field without this number having to move.
    /// </summary>
    public const int EnvelopeReserveBytes = 1024;

    /// <summary>
    /// The worst number of canonical JSON bytes one raw UTF-8 byte of patch text can become.
    /// The canonical writer serializes strings with the default JSON encoder, which escapes every
    /// non-ASCII character and several ASCII ones, <c>&lt;</c> and <c>&gt;</c> among them, to a
    /// six-byte <c>\uXXXX</c> form. One ASCII byte can therefore become six bytes, which is the
    /// worst case: a two-byte UTF-8 sequence becomes six (three per byte), a three-byte sequence
    /// becomes six (two per byte), and a four-byte sequence becomes two escapes, twelve bytes, three
    /// per byte. Nothing exceeds six, and C# source is full of the ASCII characters that reach it,
    /// so this is a realistic factor rather than a paranoid one.
    /// </summary>
    public const int WorstCaseCanonicalExpansion = 6;

    /// <summary>
    /// The raw patch budget, derived rather than chosen: the action-payload ceiling bounds canonical
    /// JSON bytes, so the raw text that fits under it is much smaller than the ceiling reads.
    /// <c>(65536 - 1024 envelope - 2 quotes) / 6 = 10751</c> bytes. Stating the ceiling as 64 KiB
    /// would be wrong by a factor of six; a patch is a small, targeted change or it is refused.
    /// </summary>
    public const int RawBudgetBytes =
        (ActionApprovalLimits.MaximumPayloadBytes - EnvelopeReserveBytes - 2) / WorstCaseCanonicalExpansion;

    /// <summary>
    /// The bounds one attempt runs under, taken from the options the rest of the source path already
    /// runs under rather than restated. The size bound and the extension set are the excerpt
    /// reader's own, so lowering either in configuration also lowers what a patch may leave behind;
    /// stating them here would let a configured bound and the bound a patch is checked against drift
    /// apart, and the drift would only show up as a file the evidence path refuses to open. The
    /// depth bound is the tree walk's own, for the same reason.
    /// </summary>
    public static SourcePatchLimits For(SourceContextOptions options, SourceWorkspaceBounds bounds) => new(
        RawBudgetBytes,
        MaximumFiles: 16,
        MaximumHunksPerFile: 32,
        MaximumPathCharacters: 200,
        MaximumPathSegments: bounds.MaxDepth,
        MaximumTargetBytes: options.MaxSourceBytes,
        AllowedExtensions: options.AllowedExtensions);
}
