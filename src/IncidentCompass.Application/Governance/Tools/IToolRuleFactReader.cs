namespace IncidentCompass.Application.Governance.Tools;

/// <summary>
/// The ledger facts a tool rule is decided from, on either governance path.
/// </summary>
/// <remarks>
/// The scope arrives parsed. An implementation is never handed the configured string and therefore
/// never decides what an unrecognized one means; <see cref="ToolRuleScopes.NarrowsToAttempt" /> is
/// the one place that says what a window is.
/// </remarks>
internal interface IToolRuleFactReader
{
    Task<int> CountAcceptedUsesAsync(
        string toolName,
        ToolRuleScope scope,
        CancellationToken cancellationToken);

    Task<bool> HasSuccessfulToolResultAsync(
        string toolName,
        ToolRuleScope scope,
        CancellationToken cancellationToken);
}
