namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The outcome of one apply attempt: either the whole diff applied and the resulting tree has an
/// identity, or nothing was applied and only a code comes back.
/// </summary>
/// <param name="Code">
/// The outcome, from the closed vocabulary described on <see cref="RemediationCodes" />. A refusal
/// is a code and nothing else: no path, no line, no byte of the diff and no byte of a file, because
/// callers log and persist this and the diff is model text about attacker-influenced incident data.
/// </param>
/// <param name="ResultTreeIdentity">
/// The identity of the tree the diff produced, recomputed by walking it rather than derived from
/// what the applier believes it wrote. <see langword="null" /> on every refusal.
/// </param>
/// <param name="FilesChanged">How many file sections were written. Zero on every refusal.</param>
/// <param name="AnswerCorrectable">
/// Whether a different answer to the same request could plausibly succeed. The adapter decides this
/// because the adapter owns the refusal vocabulary, and a caller that guessed by inspecting code
/// strings would guess wrong the first time the vocabulary grew. A malformed diff, a mismatched
/// context line and a rejected path are the model's to fix; a base that moved, a filesystem error, a
/// failed rollback and an unconfigured host are not, and reprompting on those spends budget on a
/// question the model cannot answer.
/// </param>
public sealed record RemediationApplyResult(
    string Code,
    string? ResultTreeIdentity,
    int FilesChanged,
    bool AnswerCorrectable)
{
    public static RemediationApplyResult Applied(string resultTreeIdentity, int filesChanged) =>
        new(RemediationCodes.Applied, resultTreeIdentity, filesChanged, AnswerCorrectable: false);

    public static RemediationApplyResult Refused(string code, bool answerCorrectable) =>
        new(code, null, 0, answerCorrectable);

    public bool IsApplied => ResultTreeIdentity is not null;
}
