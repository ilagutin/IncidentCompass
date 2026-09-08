using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Embeddings.OpenAi;
using IncidentCompass.Infrastructure.ModelGateway.OpenAi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IncidentCompass.UnitTests;

public sealed class OpenAiCompatibleHttpClientRegistrationTests
{
    [Fact]
    public void AddInfrastructure_AllowsLongProviderTimeoutWithoutAddingAnHttpClientDeadline()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["IncidentCompass:ModelGateway:Provider"] = "OpenAiCompatible",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:ApiKey"] = "test-model-key",
                ["IncidentCompass:ModelGateway:OpenAiCompatible:TimeoutSeconds"] = "600",
                ["IncidentCompass:Embeddings:Provider"] = "OpenAiCompatible",
                ["IncidentCompass:Embeddings:OpenAiCompatible:ApiKey"] = "test-embedding-key",
                ["IncidentCompass:Embeddings:OpenAiCompatible:TimeoutSeconds"] = "600"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.Equal(
            600,
            provider.GetRequiredService<IOptions<OpenAiCompatibleModelClientOptions>>().Value.TimeoutSeconds);
        Assert.Equal(
            600,
            provider.GetRequiredService<IOptions<OpenAiCompatibleEmbeddingClientOptions>>().Value.TimeoutSeconds);

        var clientFactory = provider.GetRequiredService<IHttpClientFactory>();
        var modelClientName = typeof(OpenAiCompatibleModelClient).Name;
        var embeddingClientName = typeof(OpenAiCompatibleEmbeddingClient).Name;

        using var modelHttpClient = clientFactory.CreateClient(modelClientName);
        using var embeddingHttpClient = clientFactory.CreateClient(embeddingClientName);

        Assert.Equal(Timeout.InfiniteTimeSpan, modelHttpClient.Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, embeddingHttpClient.Timeout);
        Assert.NotEqual(TimeSpan.FromSeconds(100), modelHttpClient.Timeout);
        Assert.NotEqual(TimeSpan.FromSeconds(100), embeddingHttpClient.Timeout);

        var handlerFactory = provider.GetRequiredService<IHttpMessageHandlerFactory>();
        HttpMessageHandler modelHandler = handlerFactory.CreateHandler(modelClientName);
        while (modelHandler is DelegatingHandler delegatingHandler)
        {
            modelHandler = delegatingHandler.InnerHandler!;
        }

        var primaryHandler = Assert.IsType<SocketsHttpHandler>(modelHandler);
        Assert.False(primaryHandler.AllowAutoRedirect);
    }
}
