using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Remediation;

namespace IncidentCompass.Worker;

/// <summary>
/// Refuses to start a Worker whose configuration enables code publication it could not carry out.
/// </summary>
/// <remarks>
/// <para>
/// Without this, a host with <c>branch_push</c> or <c>pr_create</c> allowed but no repository,
/// credential or base branch configured would start cleanly, run a triage, execute a code write,
/// propose a push, wait for a person to approve it, and only then fail at dispatch with a binding code.
/// Failing at boot turns that into a message an operator reads before anything has been proposed. It is
/// the same shape as the ticket validator, and for the same reason.
/// </para>
/// <para>
/// Both ids are checked because both need the same binding and either can be enabled on its own. The
/// message says the same thing for both and names nothing: no owner, no repository, no branch and no
/// credential, because a boot failure is read in logs an operator may not be the only reader of.
/// </para>
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
        Validate(configuration, BranchPushToolDescriptor.ToolId, "branch_push");
        Validate(configuration, PullRequestToolDescriptor.ToolId, "pr_create");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Validate(TriageConfiguration configuration, string toolId, string category)
    {
        if (!configuration.Actions.AllowedTools.Contains(toolId, StringComparer.Ordinal))
        {
            return;
        }

        if (!configuration.Tools.TryGetValue(toolId, out var tool) ||
            !string.Equals(tool.Kind, "external_action", StringComparison.Ordinal) ||
            !string.Equals(tool.Category, category, StringComparison.Ordinal) ||
            !string.Equals(
                tool.LogicalTargetId,
                BranchPushToolDescriptor.LogicalTargetId,
                StringComparison.Ordinal))
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
}
