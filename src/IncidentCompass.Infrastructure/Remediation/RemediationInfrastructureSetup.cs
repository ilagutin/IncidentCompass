using IncidentCompass.Application.Remediation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
    public static IServiceCollection AddRemediationInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Replace(ServiceDescriptor.Scoped<IRemediationWorkspace, LocalSourceRemediationWorkspace>());
        services.TryAddScoped<IRemediationDiffRepository, PostgresRemediationDiffRepository>();
        services.TryAddScoped<IRemediationPassContextRepository, PostgresRemediationPassContextRepository>();
        services.TryAddScoped<RemediationDiffRunner>();

        // The publication half. The repository, owner and credential are the ticket binding's, read
        // from the options that already carry them; the base branch is the one thing a host says here,
        // and an unset one means code publication is not configured and every call refuses. The
        // gateway is a singleton because it owns one HttpClient, exactly as the issue adapters do.
        services.AddOptions<GitHubCodePublicationOptions>()
            .Bind(configuration.GetSection(GitHubCodePublicationOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<GitHubCodePublicationOptions>,
            GitHubCodePublicationOptionsValidator>());
        services.TryAddSingleton<GitHubCodePublicationGateway>();
        services.TryAddSingleton<ICodePublicationGateway>(
            provider => provider.GetRequiredService<GitHubCodePublicationGateway>());
        services.TryAddScoped<IRemediationPredecessorReader, PostgresRemediationPredecessorReader>();
        services.TryAddScoped<IBranchPushActionHistory, PostgresBranchPushActionHistory>();
        services.TryAddScoped<IConfirmedPullRequestReader, PostgresConfirmedPullRequestReader>();

        // The publisher is bound here for the same reason as the runner: it is an Application type
        // that cannot be constructed without IRemediationDiffRepository, which only this method
        // registers. The adapter it proposes through is not bound here, because a host that cannot
        // dispatch an approved action has no business declaring one it could execute.
        services.TryAddScoped<RemediationProposalPublisher>();

        // The push publisher is bound here for the same reason: it is an Application type that cannot
        // be constructed without ports only this method registers. The adapter that executes an
        // approved push is not bound here, because a host that cannot dispatch one has no business
        // declaring a tool it could execute.
        services.TryAddScoped<BranchPushProposalPublisher>();

        // The pull-request publisher, for the same reason again: it cannot be constructed without the
        // predecessor reader this method registers and the ticket evidence resolver the ticket half
        // registers, neither of which AddApplication can satisfy on its own.
        services.TryAddScoped<PullRequestProposalPublisher>();
        return services;
    }
}
