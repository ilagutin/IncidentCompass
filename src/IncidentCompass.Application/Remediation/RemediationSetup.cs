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
        return services;
    }
}
