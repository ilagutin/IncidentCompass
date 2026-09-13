using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IncidentCompass.Infrastructure.EmbeddingModels;

internal static class LocalOnnxModelSetup
{
    /// <summary>
    /// The local embedding model store: its options, the fetcher, the store, the shared install
    /// state and the install hosted service. The hosted service is registered here, so the caller
    /// decides its place in the start order by where it calls this.
    /// </summary>
    public static IServiceCollection AddLocalOnnxModelStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<LocalOnnxEmbeddingOptions>()
            .Bind(configuration.GetSection(LocalOnnxEmbeddingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<LocalOnnxEmbeddingOptions>,
            LocalOnnxEmbeddingOptionsValidator>());

        // Redirects are followed by the fetcher, which allows only https hops; the bound on a
        // download is the install pass's timeout and the size limit, not a per-request timeout.
        // The factory's default logging handlers are removed: they log every request URI at
        // Information, and a model download hops to content delivery locations whose paths do not
        // belong in the Worker log. The fetcher's own failures name the host only.
        services
            .AddHttpClient<LocalOnnxModelFileFetcher>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false
            })
            .RemoveAllLoggers();
        services.TryAddTransient<LocalOnnxModelStore>();
        services.TryAddSingleton<LocalOnnxModelInstallState>();
        services.TryAddSingleton<LocalOnnxInstalledModelReader>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LocalOnnxModelInstallHostedService>());

        return services;
    }
}
