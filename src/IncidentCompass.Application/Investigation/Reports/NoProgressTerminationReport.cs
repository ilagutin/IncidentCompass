using IncidentCompass.Domain.Incidents;

namespace IncidentCompass.Application.Investigation.Reports;

/// <summary>
/// The report the backend itself publishes when an investigation stops making progress and no
/// recovery is left. It states no conclusion: status <c>InsufficientEvidence</c>, classification
/// <c>Unknown</c>, confidence <c>Low</c>, documentation fit <c>Missing</c>, and fixed sentences.
/// </summary>
/// <remarks>
/// <para>
/// <b>Evidence.</b> It cites only the job-level facts every investigation starts from: the trigger
/// signal and, on a re-triage, the recurrence state the repository requires a re-triage report to
/// cite. Both ground for any attempt of the job, carry no documentation status (so the documentation
/// fit the resolver derives is <c>Missing</c>), and need no quote. Attempt evidence is not cited: the
/// backend did not read it and cannot claim what it supports, and anything that fails grounding would
/// turn an honest stop into a failed attempt.
/// </para>
/// <para>
/// <b>Authorship.</b> A reader tells this report apart by durable state, not by its wording: the
/// report is created with <see cref="TriageReport.BackendAuthored"/>, so its <c>ReportPublished</c>
/// ledger rationale opens with <see cref="ReservedReportText.BackendAuthoredLedgerPrefix"/>, and a
/// <c>no_progress: terminated</c> budget event follows the publication. A model-authored report can
/// produce neither.
/// </para>
/// <para>
/// <b>Reserved text, as defence in depth.</b> <see cref="ApplyToModelAuthored"/> strips any of the
/// <see cref="ReservedLimitations"/> from a model-authored report when a limitation matches one, and
/// refuses the report when the joined limitations still contain one, when the summary matches <see cref="Summary"/>,
/// or when the summary opens with the ledger marker. Matching uses <see cref="ReservedReportText"/>.
/// </para>
/// </remarks>
internal static class NoProgressTerminationReport
{
    public const string Summary =
        "The backend ended this investigation because it stopped making progress; no conclusion was reached.";

    /// <summary>Every reason's limitation sentence, each reserved to the backend.</summary>
    public static readonly IReadOnlyList<string> ReservedLimitations =
    [
        LimitationNoRecoveryLeft,
        LimitationNoWindowLeft,
        LimitationRecoveryNotAdmitted,
        LimitationTurnLimitDuringStall,
        LimitationWorkerBudgetDuringStall
    ];

    public const string LimitationNoRecoveryLeft =
        "The backend stopped this investigation because it made no progress and no recovery attempt was left; this report is backend-authored and states no conclusion.";

    public const string LimitationNoWindowLeft =
        "The backend stopped this investigation because it made no progress and too few turns or workers remained to act on a recovery attempt; this report is backend-authored and states no conclusion.";

    public const string LimitationRecoveryNotAdmitted =
        "The backend stopped this investigation because it made no progress and the attempt budget had no room for a recovery attempt; this report is backend-authored and states no conclusion.";

    public const string LimitationTurnLimitDuringStall =
        "The backend stopped this investigation because it reached its turn limit during a stall it had detected; this report is backend-authored and states no conclusion.";

    public const string LimitationWorkerBudgetDuringStall =
        "The backend stopped this investigation because it reached its worker budget during a stall it had detected; this report is backend-authored and states no conclusion.";

    public const string RecommendedNextAction =
        "Review the triage ledger for this job, starting at its no_progress budget events, before triaging the fault again.";

    public const string Confidence = "Low";

    private const string UnknownClassification = "Unknown";

    public static string LimitationFor(NoProgressTerminationReason reason) => reason switch
    {
        NoProgressTerminationReason.NoRecoveryLeft => LimitationNoRecoveryLeft,
        NoProgressTerminationReason.NoWindowLeft => LimitationNoWindowLeft,
        NoProgressTerminationReason.RecoveryNotAdmitted => LimitationRecoveryNotAdmitted,
        NoProgressTerminationReason.TurnLimitDuringStall => LimitationTurnLimitDuringStall,
        _ => LimitationWorkerBudgetDuringStall
    };

    public static TriageReport Create(IEnumerable<TriageArtifact> jobArtifacts, NoProgressTerminationReason reason)
    {
        ArgumentNullException.ThrowIfNull(jobArtifacts);
        var evidence = jobArtifacts
            .Where(static artifact => artifact.Attempt is null &&
                artifact.Kind is ArtifactKind.TriggerSignal or ArtifactKind.RecurrenceState)
            .OrderBy(static artifact => artifact.Kind)
            .Select(static artifact => new TriageReportEvidenceReference("artifact:" + artifact.Id, null))
            .ToArray();
        return new TriageReport(
            TriageReportStatus.InsufficientEvidence,
            Summary,
            UnknownClassification,
            Confidence,
            evidence,
            [LimitationFor(reason)],
            RecommendedNextAction)
        {
            DocumentationFit = DocumentationFitStatus.Missing,
            BackendAuthored = true
        };
    }

    /// <summary>
    /// Settles reserved text on a model-authored report: strips a matching limitation and refuses the
    /// report when reserved text remains anywhere a reader would take it for the backend's.
    /// </summary>
    public static TriageReport ApplyToModelAuthored(TriageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var stripped = WithoutReservedLimitation(report) with { BackendAuthored = false };
        if (ReservedReportText.EqualsReserved(stripped.Summary, Summary) ||
            ReservedReportText.StartsWithBackendMarker(stripped.Summary) ||
            ReservedLimitations.Any(reserved => ReservedReportText.ContainsReserved(string.Join(" ", stripped.Limitations), reserved)))
        {
            throw new TriageReportValidationException(ReservedReportText.ReservedTextRefusal);
        }

        return stripped;
    }

    private static TriageReport WithoutReservedLimitation(TriageReport report) =>
        report with
        {
            Limitations = report.Limitations
                .Where(static limitation => !ReservedLimitations.Any(reserved => ReservedReportText.EqualsReserved(limitation, reserved)))
                .ToList()
        };
}
