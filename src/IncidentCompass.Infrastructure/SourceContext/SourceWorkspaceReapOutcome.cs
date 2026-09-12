namespace IncidentCompass.Infrastructure.SourceContext;

/// <summary>
/// What one abandoned-workspace reaping run did.
/// </summary>
/// <param name="Code">One value from <see cref="SourceWorkspaceReapCodes"/>.</param>
/// <param name="Examined">Prefixed directories the run looked at before it stopped.</param>
/// <param name="Reaped">Directories the run deleted.</param>
/// <param name="Kept">
/// Directories the run deliberately left alone: too young by the instant in their name, touched too
/// recently, or named in a way this code cannot date. All three are the same decision - not mine to
/// delete - and separating them would invite a reader to treat one of them as a failure.
/// </param>
/// <param name="Failed">Directories the run tried to delete and could not.</param>
/// <remarks>
/// The counters are a report, not a claim about the backlog. A run that spends its budget says so
/// through <paramref name="Reaped"/> reaching the configured bound, and the rest waits for the next
/// pass.
/// </remarks>
public sealed record SourceWorkspaceReapOutcome(
    string Code,
    int Examined,
    int Reaped,
    int Kept,
    int Failed)
{
    public static SourceWorkspaceReapOutcome Refused(string code) => new(code, 0, 0, 0, 0);
}
