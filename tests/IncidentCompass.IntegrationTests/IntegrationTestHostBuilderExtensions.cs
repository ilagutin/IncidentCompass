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
}
