namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// The single definition of the model-call kinds recorded on <see cref="TriageJobCallContext"/>. The
/// kind reaches structured logs and the redacted <c>ModelCall</c> ledger metadata, so cost roll-ups
/// group on these exact strings and they must be spelled in one place only.
/// </summary>
internal static class TriageModelCallKinds
{
    public const string Orchestrator = "orchestrator";

    public const string Worker = "worker";

    /// <summary>
    /// A post-report remediation call: the one that asks for a unified diff. It is a separate kind
    /// so cost roll-ups can tell what an incident spent producing its report from what it spent
    /// proposing a change, while both keep running on the same admission, deadline and accounting.
    /// </summary>
    public const string Remediation = "remediation";
}
