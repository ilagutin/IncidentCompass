using IncidentCompass.Application.Governance.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentCompass.Application.Memory;

internal static class MemorySetup
{
    /// <summary>
    /// Registers the <c>memory_search</c> descriptor only. The descriptor is composed on every host,
    /// because a configuration naming the tool has to validate everywhere; the tool itself embeds
    /// the worker query, so <see cref="Setup.AddMemorySearchTool" /> registers it on the one host
    /// composed with an embedding client.
    /// </summary>
    public static IServiceCollection AddMemoryCore(this IServiceCollection services)
    {
        services.AddSingleton(new AgentToolDescriptor("memory_search", AgentToolCapability.ImmediateRead));

        return services;
    }
}
