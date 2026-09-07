using System.Globalization;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Memory;
using IncidentCompass.Infrastructure;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class MemorySeedRouteGuardTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task StartAsync_MissingEmbeddingRouteFailsBeforeEmbeddingOrMemoryRepositoryWork()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        var sourceDirectory = CreateSourceDirectory();
        var configuration = await ReadCurrentConfigurationAsync(connectionString, sourceDirectory);
        var routeId = configuration.Tools["memory_search"].EmbeddingRouteId!;
        var missingRouteConfiguration = configuration with
        {
            Routes = configuration.Routes
                .Where(pair => !string.Equals(pair.Key, routeId, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
        var embeddingClient = new CountingEmbeddingClient();
        var memoryRepository = new ThrowingMemoryRepository();
        using var host = CreateHost(
            connectionString,
            sourceDirectory,
            embeddingClient,
            services =>
            {
                services.RemoveAll<ITriageConfigurationRepository>();
                services.AddSingleton<ITriageConfigurationRepository>(
                    new StaticTriageConfigurationRepository(missingRouteConfiguration));
                services.RemoveAll<IMemoryRepository>();
                services.AddSingleton<IMemoryRepository>(memoryRepository);
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(routeId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Tools.memory_search.EmbeddingRouteId", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, embeddingClient.CallCount);
        Assert.Equal(0, memoryRepository.CallCount);
    }

    private static async Task<TriageConfiguration> ReadCurrentConfigurationAsync(
        string connectionString,
        string sourceDirectory)
    {
        using var host = CreateHost(connectionString, sourceDirectory, new CountingEmbeddingClient());
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<ITriageConfigurationRepository>()
            .GetCurrentAsync(TestContext.Current.CancellationToken);
    }

    private static IHost CreateHost(
        string connectionString,
        string sourceDirectory,
        IEmbeddingClient embeddingClient,
        Action<IServiceCollection>? configureServices = null)
    {
        return new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:IncidentCompass"] = connectionString,
                    ["IncidentCompass:ConfigSource:Path"] = Path.Combine(RepositoryRootLocator.Find(), "config", "incidentcompass.config.json"),
                    ["IncidentCompass:Memory:Seed:Enabled"] = "true",
                    ["IncidentCompass:Memory:Seed:TenantId"] = "local",
                    ["IncidentCompass:Memory:Seed:Owner"] = "route-guard",
                    ["IncidentCompass:Memory:Seed:RuntimeResyncEnabled"] = "false",
                    ["IncidentCompass:Memory:Seed:RuntimeResyncIntervalSeconds"] = 1.ToString(CultureInfo.InvariantCulture),
                    ["IncidentCompass:Memory:Seed:SourceDirectory"] = sourceDirectory
                });
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddSingleton<IEmbeddingClient>(embeddingClient);
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
                configureServices?.Invoke(services);
            })
            .Build();
    }

    private static string CreateSourceDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "incidentcompass-memory-route-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class StaticTriageConfigurationRepository(TriageConfiguration configuration)
        : ITriageConfigurationRepository
    {
        public Task<TriageConfiguration> GetCurrentAsync(CancellationToken cancellationToken) =>
            Task.FromResult(configuration);

        public Task<TriageConfiguration> GetByHashAsync(string configHash, CancellationToken cancellationToken) =>
            Task.FromResult(configuration);
    }

    private sealed class CountingEmbeddingClient : IEmbeddingClient
    {
        public int CallCount { get; private set; }

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new EmbeddingResponse([1f], request.Model, "test", 1, request.CorrelationId));
        }
    }

    private sealed class ThrowingMemoryRepository : IMemoryRepository
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<MemorySearchMatch>> SearchAsync(
            MemorySearchRequest request,
            CancellationToken cancellationToken) => Fail<IReadOnlyList<MemorySearchMatch>>();

        public Task<bool> SeedItemExistsAsync(
            string owner,
            MemorySeedItem item,
            CancellationToken cancellationToken) => Fail<bool>();

        public Task ReconcileSeedCorpusAsync(
            MemorySeedCorpus corpus,
            CancellationToken cancellationToken) => Fail();

        private Task Fail()
        {
            CallCount++;
            throw new InvalidOperationException("Memory repository must not be called when the route is missing.");
        }

        private Task<T> Fail<T>()
        {
            CallCount++;
            throw new InvalidOperationException("Memory repository must not be called when the route is missing.");
        }
    }
}
