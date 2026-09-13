using IncidentCompass.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace IncidentCompass.IntegrationTests;

internal static class IntegrationTestHostBuilderExtensions
{
    public static IWebHostBuilder UseExplicitMockProviders(this IWebHostBuilder builder)
    {
        builder.UseSetting("IncidentCompass:ModelGateway:Provider", "Mock");
        builder.UseSetting("IncidentCompass:Embeddings:Provider", "Mock");
        return builder;
    }

    /// <summary>
    /// Adds the Worker-only embedding host to an Api test host. Running the Worker path inside the Api
    /// test host is a pre-existing test convention; this seam supplies the embedding client, the memory
    /// seed pass and the <c>memory_search</c> tool that path needs. It is not product composition: the
    /// real Api composes none of them.
    /// </summary>
    public static IWebHostBuilder UseWorkerModelHost(this IWebHostBuilder builder)
    {
        builder.ConfigureServices((context, services) => services.AddEmbeddingHost(context.Configuration));
        return builder;
    }
}
