namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The durable record of one remediation diff: the exact change, the tree it was applied to, the
/// tree it produced, and what was and was not verified about it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it is for.</b> Two readers, and the fields are chosen for them. A later approval needs the
/// exact bytes a human would approve and the base identity that approval is bound to, because an
/// approved diff applied to a tree its approver never saw is the failure this record exists to make
/// impossible. A human reviewer needs enough to judge the change without a join: which incident and
/// report it came from, which service and release it is about, what wrote it, how much it touches,
/// and what has not been checked.
/// </para>
/// <para>
/// <b>Where the diff body lives, and the arithmetic.</b> Here, and nowhere else. It is not in a log
/// line, not in a ledger rationale and not in a refusal code, all of which are content-free by
/// design. It is here because it fits: the action-payload ceiling is 64 KiB of canonical JSON, the
/// canonical writer can turn one raw byte into a six-byte <c>\uXXXX</c> escape, and reserving 1 KiB
/// for the fields that travel beside the diff and two bytes for its quotes leaves
/// <c>(65536 - 1024 - 2) / 6 = 10751</c> raw UTF-8 bytes. The parser refuses anything larger before
/// this record can be built, so a recorded diff is by construction one an approval can carry, and
/// <see cref="PatchBytes" /> is the measured proof rather than a promise. Reading the ceiling as
/// 64 KiB of diff text would be wrong by a factor of six.
/// </para>
/// <para>
/// <b>What it must never carry.</b> No file body beyond the lines the diff itself quotes, no rendered
/// prompt, no instructions, no host path, no provider endpoint and no credential. Every field is
/// bounded: the identities are fixed-width digests, the counts are integers, and the only free text
/// is the diff, under the bound above.
/// </para>
/// </remarks>
/// <param name="Id">Identity of this record.</param>
/// <param name="TenantId">The fault's tenant. Every read of this record is tenant-scoped.</param>
/// <param name="ReportId">The grounded report the change was derived from.</param>
/// <param name="JobId">The triage job that produced that report.</param>
/// <param name="Attempt">That job's attempt, so the model calls in the ledger can be found.</param>
/// <param name="ServiceName">The faulting service, and half of what selects the checkout.</param>
/// <param name="Release">The configured current release, and the other half.</param>
/// <param name="BaseTreeIdentity">
/// The content identity of the tree the diff was prepared against and applied to. This is the value
/// a later application must compare its own workspace against, and refuse on a difference.
/// </param>
/// <param name="ResultTreeIdentity">
/// The content identity of the tree the diff produced, recomputed by walking that tree rather than
/// derived from what the applier believed it wrote.
/// </param>
/// <param name="FilesChanged">How many file sections the diff wrote.</param>
/// <param name="PatchBytes">Raw UTF-8 bytes of <see cref="PatchText" />.</param>
/// <param name="PatchText">The exact unified diff, byte for byte as it applied.</param>
/// <param name="RouteId">The configured route that answered, so provenance needs no join.</param>
/// <param name="Model">The provider model name that wrote the diff. Not a credential and not an endpoint.</param>
/// <param name="ValidationCode">
/// What the backend concluded about the diff, from the closed vocabulary. A recorded diff always
/// carries <see cref="RemediationCodes.Applied" />: a diff that did not apply produces no record at
/// all, so this field is never a way to store a failure and call it evidence.
/// </param>
/// <param name="CreatedAtUtc">When the record was made.</param>
public sealed record RemediationDiff(
    Guid Id,
    string TenantId,
    Guid ReportId,
    Guid JobId,
    int Attempt,
    string ServiceName,
    string Release,
    string BaseTreeIdentity,
    string ResultTreeIdentity,
    int FilesChanged,
    int PatchBytes,
    string PatchText,
    string RouteId,
    string Model,
    string ValidationCode,
    DateTimeOffset CreatedAtUtc)
{
    /// <summary>
    /// The only <see cref="TestOutcome" /> this release produces. Nothing runs a test: no process is
    /// started anywhere in the product, and the architecture guard that fails the build when process
    /// I/O appears in the Application project stands unchanged. A record therefore carries no
    /// evidence that the change builds, passes anything, or is correct.
    /// </summary>
    public const string TestNotExecuted = "not_executed";

    /// <summary>
    /// What running a test would have proved. Always <see cref="TestNotExecuted" /> here.
    /// </summary>
    /// <remarks>
    /// It is an <c>init</c> property defaulted to the honest answer rather than a positional member,
    /// so no call site in this release can claim a test ran by filling in a parameter, and the day a
    /// release does run one it has to say so deliberately.
    /// </remarks>
    public string TestOutcome { get; init; } = TestNotExecuted;

    /// <summary>
    /// Which configured test command ran. Always <see langword="null" /> here, and it means "nothing
    /// was run", which is a different claim from "something ran and said nothing".
    /// </summary>
    public string? TestCommandId { get; init; }
}
