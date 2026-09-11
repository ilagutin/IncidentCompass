using IncidentCompass.Application.Remediation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Registers the remediation feature's Infrastructure half.
/// </summary>
/// <remarks>
/// The workspace adapter replaces the Application default unconditionally, exactly as the source
/// lookup adapter does, because "is a monitored checkout configured on this host" is a question the
/// adapter answers per call from options that can change, not a question a registration can answer
/// once at startup. With no workspace root and no matching root the adapter refuses with the same
/// closed code the default returns. The diff repository has no Application default, so registering
/// it here is what makes a produced diff durable at all.
/// <para>
/// Options binding is not repeated here: the adapter reads <c>SourceContextOptions</c>, which
/// <c>AddSourceContextInfrastructure</c> already binds and validates on start.
/// </para>
/// <para>
/// <see cref="RemediationDiffRunner" /> is bound here rather than from <c>AddApplication</c>. It is
/// an Application type, but it cannot be constructed without <c>InvestigationModelCaller</c> and
/// <c>TriageLedgerAppender</c> - both bound only by <c>AddGovernedInvestigationServices</c> - and
/// without <see cref="IRemediationDiffRepository" />, which this method registers. <c>AddApplication</c>
/// has to stay resolvable on its own for the memory-only composition path, so the runner is
/// registered next to the dependencies that alone can satisfy it, the same reason the retention
/// operations are bound from <c>AddInfrastructure</c> instead of <c>AddApplication</c>.
/// </para>
/// </remarks>
internal static class RemediationInfrastructureSetup
{
    public static IServiceCollection AddRemediationInfrastructure(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Scoped<IRemediationWorkspace, LocalSourceRemediationWorkspace>());
        services.TryAddScoped<IRemediationDiffRepository, PostgresRemediationDiffRepository>();
        services.TryAddScoped<IRemediationPassContextRepository, PostgresRemediationPassContextRepository>();
        services.TryAddScoped<RemediationDiffRunner>();
        return services;
    }
}
