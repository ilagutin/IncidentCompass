using IncidentCompass.Application.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IncidentCompass.Infrastructure.Memory;

internal static class MemoryInfrastructureSetup
{
    public static IServiceCollection AddMemoryInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MemorySeedOptions>(configuration.GetSection(MemorySeedOptions.SectionName));
        services.TryAddSingleton<MemorySeedSyncStatus>();
        services.TryAddSingleton<MemorySeedSyncStatusPersistence>();
        services.TryAddSingleton<IMemorySeedSyncStatus>(serviceProvider =>
            serviceProvider.GetRequiredService<MemorySeedSyncStatus>());
        services.TryAddScoped<PostgresMemorySeedSyncStatusStore>();
        services.TryAddScoped<IMemorySeedSyncStatusReader>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresMemorySeedSyncStatusStore>());
        services.TryAddScoped<IMemorySeedSyncStatusWriter>(serviceProvider =>
            serviceProvider.GetRequiredService<PostgresMemorySeedSyncStatusStore>());
        services.TryAddScoped<IMemoryRepository, PostgresMemoryRepository>();
        services.TryAddScoped<MemorySeedSynchronizer>();
        services.TryAddScoped<IMemoryCorpusStatusReader, MemoryCorpusStatusReader>();
        services.AddHostedService<MemorySeedHostedService>();

        return services;
    }
}
