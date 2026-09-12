using IncidentCompass.Application.SourceContext;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.SourceContext;

internal static class SourceContextInfrastructureSetup
{
    public static IServiceCollection AddSourceContextInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<SourceContextOptions>()
            .Bind(configuration.GetSection(SourceContextOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<SourceContextOptions>,
            SourceContextOptionsValidator>());
        services
            .AddOptions<SourceWorkspaceRetentionOptions>()
            .Bind(configuration.GetSection(SourceWorkspaceRetentionOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<SourceWorkspaceRetentionOptions>,
            SourceWorkspaceRetentionOptionsValidator>());
        services.Replace(ServiceDescriptor.Scoped<ISourceContextLookup, LocalSourceContextLookup>());

        // Bound here rather than in AddApplication: the operation is about directories under a root
        // only this layer's options name, and AddApplication has to stay resolvable on its own for
        // the memory-only composition path. Same reasoning as the two row retention operations.
        services.TryAddScoped<AbandonedSourceWorkspaceReaper>();
        return services;
    }
}
