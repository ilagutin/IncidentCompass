using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// Registers the remediation feature's Application half.
/// </summary>
/// <remarks>
/// The workspace defaults to the unavailable adapter, so a host with no monitored checkout gets a
/// closed outcome rather than a resolution failure, and Infrastructure replaces it when one is
/// configured. <see cref="RemediationDiffRunner" /> is not registered here: it depends on
/// <c>InvestigationModelCaller</c> and <c>TriageLedgerAppender</c>, both bound only from
/// <c>AddGovernedInvestigationServices</c> in <c>IncidentCompass.Infrastructure.Setup</c>, and on
/// <see cref="IRemediationDiffRepository" />, which has no Application default because a pass that
/// could not persist what it produced must not silently drop it. Registering the runner here would
/// break the memory-only composition path, which builds <c>AddApplication</c> on its own; it is
/// registered next to those dependencies in <c>AddRemediationInfrastructure</c> instead, the same
/// reasoning that keeps the retention operations out of this file.
/// </remarks>
internal static class RemediationSetup
{
    public static IServiceCollection AddRemediationCore(this IServiceCollection services)
    {
        services.TryAddScoped<IRemediationWorkspace, UnavailableRemediationWorkspace>();

        // The descriptor is a value, not a port, so it belongs here beside the other backend tool
        // descriptors: configuration load validation refuses a `remediation_diff` tool entry that
        // names no registered descriptor, and that validation runs wherever a configuration is
        // loaded, not only where a pass can run.
        services.AddSingleton(RemediationDiffToolDescriptor.Descriptor);

        // The approval half is a second descriptor for the same reason: configuration load
        // validation refuses a `remediation_apply` tool entry that names no registered descriptor,
        // and that validation runs wherever a configuration is loaded, not only where a pass or a
        // dispatch can run. The adapter behind it is registered by the host that can execute one.
        services.AddSingleton(RemediationApplyToolDescriptor.Descriptor);

        // And the publication half, for the same reason again: a configuration naming `branch_push`
        // has to validate on every host that loads one, including the Api, which can neither propose
        // nor dispatch a push. The adapter and the gateway behind this descriptor are registered only
        // by the host that can execute one.
        services.AddSingleton(BranchPushToolDescriptor.Descriptor);

        // And the last link, for the same reason a third time: a configuration naming `pr_create`
        // has to validate on every host that loads one, including the Api, which can neither propose
        // nor dispatch one.
        services.AddSingleton(PullRequestToolDescriptor.Descriptor);
        return services;
    }
}
