using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure.EmbeddingModels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.Relevance.LocalOnnx;

internal static class LocalOnnxRelevanceJudgeSetup
{
    /// <summary>
    /// The local relevance judge: its options, its own install state, the install pass, the runtime
    /// that holds the loaded cross-encoder and the adapter bound to
    /// <see cref="IMemoryRelevanceJudge" />. It reuses the model store and the fetcher the embedding
    /// host already registered, because a pinned artifact set is a pinned artifact set whatever it
    /// is for.
    /// <para>
    /// Composed only through <c>AddRelevanceJudge</c>, when the configured judge provider is the local
    /// one, and only where the embedding host is composed, which is the Worker. Its options
    /// validator has no provider gate, so registering it anywhere a host without a judge would see
    /// it would stop that host; it is registered here and nowhere else.
    /// </para>
    /// <para>
    /// The install hosted service is registered last on purpose. The generic host starts hosted
    /// services in registration order, so this keeps the embedding model's install and the memory
    /// seed pass in front of it: the seed pass must still find the embedding model installed or find
    /// its named failure, and nothing on the seed path scores relevance. The judge's own install is
    /// the larger download of the two, and putting it first would delay the corpus the Worker
    /// actually serves from behind a model nothing has asked for yet.
    /// </para>
    /// </summary>
    public static IServiceCollection AddLocalOnnxRelevanceJudge(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<LocalOnnxRelevanceJudgeOptions>()
            .Bind(configuration.GetSection(LocalOnnxRelevanceJudgeOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<LocalOnnxRelevanceJudgeOptions>,
            LocalOnnxRelevanceJudgeHostOptionsValidator>());

        services.TryAddSingleton<LocalOnnxRelevanceJudgeInstallState>();

        // Built by hand rather than by the container's constructor selection: the reader's install
        // state parameter is typed as the shared LocalOnnxModelInstallState, and resolving that type
        // would hand the judge the embedding model's state.
        services.TryAddSingleton(serviceProvider => new LocalOnnxInstalledRelevanceJudgeReader(
            serviceProvider.GetRequiredService<IOptions<LocalOnnxRelevanceJudgeOptions>>(),
            serviceProvider.GetRequiredService<LocalOnnxRelevanceJudgeInstallState>(),
            serviceProvider.GetRequiredService<LocalOnnxModelStore>()));
        services.TryAddSingleton<LocalOnnxRelevanceJudgeRuntime>();
        services.TryAddSingleton<IMemoryRelevanceJudge, LocalOnnxRelevanceJudgeClient>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            LocalOnnxRelevanceJudgeInstallHostedService>());

        return services;
    }
}
