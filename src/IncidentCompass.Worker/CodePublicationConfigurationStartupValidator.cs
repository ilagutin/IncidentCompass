using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Remediation;

namespace IncidentCompass.Worker;

/// <summary>
/// Refuses to start a Worker whose configuration enables branch pushes it could not make.
/// </summary>
/// <remarks>
/// Without this, a host with <c>branch_push</c> allowed but no repository, credential or base branch
/// configured would start cleanly, run a triage, execute a code write, propose a push, wait for a
/// person to approve it, and only then fail at dispatch with a binding code. Failing at boot turns
/// that into a message an operator reads before anything has been proposed. It is the same shape as
/// the ticket validator, and for the same reason.
/// </remarks>
internal sealed class CodePublicationConfigurationStartupValidator(
    ITriageConfigurationRepository configurationRepository,
    ICodePublicationGateway gateway) : IHostedService
{
    private const string BindingError =
        "GitHub code publication binding does not match an enabled branch push action.";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurationRepository.GetCurrentAsync(cancellationToken);
        if (!configuration.Actions.AllowedTools.Contains(
                BranchPushToolDescriptor.ToolId, StringComparer.Ordinal))
        {
            return;
        }

        if (!configuration.Tools.TryGetValue(BranchPushToolDescriptor.ToolId, out var tool) ||
            !string.Equals(tool.Kind, "external_action", StringComparison.Ordinal) ||
            !string.Equals(tool.Category, "branch_push", StringComparison.Ordinal) ||
            !string.Equals(
                tool.LogicalTargetId, BranchPushToolDescriptor.LogicalTargetId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(BindingError);
        }

        if (!string.Equals(configuration.Actions.DefaultMode, "disabled", StringComparison.Ordinal) &&
            !string.Equals(tool.Mode, "disabled", StringComparison.Ordinal) &&
            !gateway.IsConfigured)
        {
            throw new InvalidOperationException(BindingError);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
