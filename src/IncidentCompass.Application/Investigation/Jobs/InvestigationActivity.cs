namespace IncidentCompass.Application.Investigation.Jobs;

/// <summary>
/// Counts of what one investigation attempt has done, kept beside the progress record for the
/// recovery summary and the termination audit. It holds names and numbers only: role and tool names
/// come from the configuration, never from a model, and nothing here carries an argument, a task or
/// an output.
/// </summary>
internal sealed class InvestigationActivity
{
    private readonly SortedSet<string> delegatedRoles = new(StringComparer.Ordinal);

    private readonly SortedDictionary<string, int> refusals = new(StringComparer.Ordinal);

    public int TurnsCompleted { get; private set; }

    public int DelegationsRun { get; private set; }

    public int ToolCallsExecuted { get; private set; }

    public int RefusedCalls { get; private set; }

    public int WorkersStopped { get; private set; }

    public int RecoveriesUsed { get; private set; }

    public IReadOnlyCollection<string> DelegatedRoles => delegatedRoles;

    /// <summary>Refused equivalent calls by <c>role/tool</c>, in ordinal order.</summary>
    public IReadOnlyDictionary<string, int> RefusalsByCall => refusals;

    public void RecordTurnCompleted() => TurnsCompleted++;

    public void RecordDelegation(string roleName)
    {
        DelegationsRun++;
        delegatedRoles.Add(roleName);
    }

    public void RecordToolCallExecuted() => ToolCallsExecuted++;

    public void RecordRefusal(string roleName, string toolName)
    {
        RefusedCalls++;
        var key = roleName + "/" + toolName;
        refusals[key] = refusals.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    public void RecordWorkerStopped() => WorkersStopped++;

    public void RecordRecoveryUsed() => RecoveriesUsed++;
}
