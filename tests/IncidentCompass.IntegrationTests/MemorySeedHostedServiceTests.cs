using System.Diagnostics;
using System.Globalization;
using IncidentCompass.Application.Core.Embeddings;
using IncidentCompass.Infrastructure;
using IncidentCompass.Infrastructure.Memory;
using IncidentCompass.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class MemorySeedHostedServiceTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task StartAsync_TwoHostsSeedSameDatabaseConcurrently_SeedsCorpusOnce()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        var firstEmbeddingClient = new CountingEmbeddingClient();
        var secondEmbeddingClient = new CountingEmbeddingClient();
        using var first = CreateHost(connectionString, sourceDirectory, firstEmbeddingClient);
        using var second = CreateHost(connectionString, sourceDirectory, secondEmbeddingClient);

        await Task.WhenAll(
            first.StartAsync(TestContext.Current.CancellationToken),
            second.StartAsync(TestContext.Current.CancellationToken));

        await Task.WhenAll(
            first.StopAsync(TestContext.Current.CancellationToken),
            second.StopAsync(TestContext.Current.CancellationToken));
        var counts = await ReadMemoryCountsAsync(connectionString);

        Assert.Equal(2, counts.Items);
        Assert.Equal(2, counts.Chunks);
        Assert.Equal(2, counts.Sources);
        Assert.Equal(1, await ReadActiveGenerationCountAsync(connectionString, "default"));
    }

    [DockerAvailableFact]
    public async Task StartAsync_AlreadySeededDatabase_SkipsExistingContentHashWithoutEmbedding()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        var embeddingClient = new CountingEmbeddingClient();
        using (var first = CreateHost(connectionString, sourceDirectory, embeddingClient))
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, embeddingClient.CallCount);

        using (var second = CreateHost(connectionString, sourceDirectory, embeddingClient))
        {
            await second.StartAsync(TestContext.Current.CancellationToken);
            await second.StopAsync(TestContext.Current.CancellationToken);
        }

        var counts = await ReadMemoryCountsAsync(connectionString);
        Assert.Equal(2, embeddingClient.CallCount);
        Assert.Equal(2, counts.Items);
        Assert.Equal(2, counts.Chunks);
        Assert.Equal(2, counts.Sources);
        Assert.Equal(1, await ReadActiveGenerationCountAsync(connectionString, "default"));
    }

    [DockerAvailableFact]
    public async Task StartAsync_EditedFileUpdatesSameItemAndReembeds()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        var embeddingClient = new CountingEmbeddingClient();
        using (var first = CreateHost(connectionString, sourceDirectory, embeddingClient))
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        await File.WriteAllTextAsync(
            Path.Combine(sourceDirectory, "runbooks", "checkout-timeout.md"),
            """
            ---
            kind: Runbook
            service: checkout-api
            component: payments
            release: 0.2
            tags: [checkout, timeout]
            ---

            # Checkout Timeout Runbook

            Updated guidance uses the circuit-breaker dashboard before retrying payments.
            """,
            TestContext.Current.CancellationToken);

        using (var second = CreateHost(connectionString, sourceDirectory, embeddingClient))
        {
            await second.StartAsync(TestContext.Current.CancellationToken);
            await second.StopAsync(TestContext.Current.CancellationToken);
        }

        var counts = await ReadMemoryCountsAsync(connectionString);
        var state = await ReadSeedStateAsync(connectionString, "runbooks/checkout-timeout.md");
        Assert.Equal(3, embeddingClient.CallCount);
        Assert.Equal(2, counts.Items);
        Assert.Equal(2, counts.Chunks);
        Assert.Equal(2, state.Version);
        Assert.Contains("Updated guidance", state.Content, StringComparison.Ordinal);
        Assert.Equal("0.2", state.ReleaseName);
    }

    [DockerAvailableFact]
    public async Task StartAsync_RemovedFileIsDeactivatedAndStopsContributingChunks()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        var embeddingClient = new CountingEmbeddingClient();
        using (var first = CreateHost(connectionString, sourceDirectory, embeddingClient))
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        File.Delete(Path.Combine(sourceDirectory, "incidents", "provider-unavailable.md"));
        using (var second = CreateHost(connectionString, sourceDirectory, embeddingClient))
        {
            await second.StartAsync(TestContext.Current.CancellationToken);
            await second.StopAsync(TestContext.Current.CancellationToken);
        }

        var counts = await ReadMemoryCountsAsync(connectionString);
        var removed = await ReadSeedStateAsync(connectionString, "incidents/provider-unavailable.md");
        Assert.Equal(2, embeddingClient.CallCount);
        Assert.Equal(1, counts.Items);
        Assert.Equal(1, counts.Chunks);
        Assert.False(removed.IsActive);
    }

    [DockerAvailableFact]
    public async Task StartAsync_DivergentOwnerCannotDeactivatePrimaryCorpus()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var primaryDirectory = await CreateSeedDirectoryAsync();
        using (var primary = CreateHost(connectionString, primaryDirectory, new CountingEmbeddingClient(), "primary"))
        {
            await primary.StartAsync(TestContext.Current.CancellationToken);
            await primary.StopAsync(TestContext.Current.CancellationToken);
        }

        var divergentDirectory = Path.Combine(Path.GetTempPath(), "incidentcompass-memory-divergent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(divergentDirectory, "runbooks"));
        File.Copy(
            Path.Combine(primaryDirectory, "runbooks", "checkout-timeout.md"),
            Path.Combine(divergentDirectory, "runbooks", "checkout-timeout.md"));
        using (var secondary = CreateHost(connectionString, divergentDirectory, new CountingEmbeddingClient(), "secondary"))
        {
            await secondary.StartAsync(TestContext.Current.CancellationToken);
            await secondary.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(2, await ReadActiveOwnerCountAsync(connectionString, "primary"));
        Assert.Equal(1, await ReadActiveOwnerCountAsync(connectionString, "secondary"));
        Assert.Equal(1, await ReadActiveGenerationCountAsync(connectionString, "primary"));
        Assert.Equal(1, await ReadActiveGenerationCountAsync(connectionString, "secondary"));
    }

    [DockerAvailableFact]
    public async Task StartAsync_MissingRootLeavesPreviousGenerationActive()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        using (var first = CreateHost(connectionString, sourceDirectory, new CountingEmbeddingClient()))
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        Directory.Delete(sourceDirectory, recursive: true);
        using var failing = CreateHost(connectionString, sourceDirectory, new CountingEmbeddingClient());

        var exception = await Record.ExceptionAsync(() => failing.StartAsync(TestContext.Current.CancellationToken));

        Assert.IsType<DirectoryNotFoundException>(exception);
        Assert.Equal(2, await ReadActiveOwnerCountAsync(connectionString, "default"));
        Assert.Equal(1, await ReadActiveGenerationCountAsync(connectionString, "default"));
    }

    [DockerAvailableFact]
    public async Task StartAsync_EmbeddingFailureDoesNotPublishPartialCorpus()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        using (var first = CreateHost(connectionString, sourceDirectory, new CountingEmbeddingClient()))
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        var original = await ReadSeedStateAsync(connectionString, "runbooks/checkout-timeout.md");
        await File.AppendAllTextAsync(
            Path.Combine(sourceDirectory, "runbooks", "checkout-timeout.md"),
            "\nUpdated before a failing second embedding.",
            TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            Path.Combine(sourceDirectory, "incidents", "provider-unavailable.md"),
            "\nUpdated before a failing second embedding.",
            TestContext.Current.CancellationToken);
        using var failing = CreateHost(connectionString, sourceDirectory, new FailAfterEmbeddingClient(1));

        var exception = await Record.ExceptionAsync(() => failing.StartAsync(TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(2, await ReadActiveOwnerCountAsync(connectionString, "default"));
        Assert.Equal(1, await ReadActiveGenerationCountAsync(connectionString, "default"));
        var current = await ReadSeedStateAsync(connectionString, "runbooks/checkout-timeout.md");
        Assert.Equal(original.Content, current.Content);
    }
    [DockerAvailableFact]
    public async Task StartAsync_EmptyRootDoesNotDeactivateCurrentCorpus()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        using (var first = CreateHost(connectionString, sourceDirectory, new CountingEmbeddingClient()))
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        var emptyDirectory = Path.Combine(Path.GetTempPath(), "incidentcompass-memory-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDirectory);
        using var failing = CreateHost(connectionString, emptyDirectory, new CountingEmbeddingClient());

        var exception = await Record.ExceptionAsync(() => failing.StartAsync(TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(2, await ReadActiveOwnerCountAsync(connectionString, "default"));
        Assert.Equal(1, await ReadActiveGenerationCountAsync(connectionString, "default"));
    }

    [DockerAvailableFact]
    public async Task StartAsync_MissingExistingCategoryDoesNotDeactivateCurrentCorpus()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        using (var first = CreateHost(connectionString, sourceDirectory, new CountingEmbeddingClient()))
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        var partialDirectory = Path.Combine(Path.GetTempPath(), "incidentcompass-memory-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(partialDirectory, "runbooks"));
        File.Copy(
            Path.Combine(sourceDirectory, "runbooks", "checkout-timeout.md"),
            Path.Combine(partialDirectory, "runbooks", "checkout-timeout.md"));
        using var failing = CreateHost(connectionString, partialDirectory, new CountingEmbeddingClient());

        var exception = await Record.ExceptionAsync(() => failing.StartAsync(TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal(2, await ReadActiveOwnerCountAsync(connectionString, "default"));
        Assert.Equal(1, await ReadActiveGenerationCountAsync(connectionString, "default"));
    }
    [DockerAvailableFact]
    public async Task StartAsync_ConcurrentPartialScanCannotDeactivateCompleteOwnerGeneration()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        using (var first = CreateHost(connectionString, sourceDirectory, new CountingEmbeddingClient()))
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            await first.StopAsync(TestContext.Current.CancellationToken);
        }

        var partialDirectory = Path.Combine(Path.GetTempPath(), "incidentcompass-memory-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(partialDirectory, "runbooks"));
        File.Copy(
            Path.Combine(sourceDirectory, "runbooks", "checkout-timeout.md"),
            Path.Combine(partialDirectory, "runbooks", "checkout-timeout.md"));
        using var complete = CreateHost(connectionString, sourceDirectory, new CountingEmbeddingClient());
        using var partial = CreateHost(connectionString, partialDirectory, new CountingEmbeddingClient());

        var completeStart = complete.StartAsync(TestContext.Current.CancellationToken);
        var partialResult = Record.ExceptionAsync(() => partial.StartAsync(TestContext.Current.CancellationToken)).AsTask();
        await Task.WhenAll(completeStart, partialResult);

        Assert.IsType<InvalidOperationException>(await partialResult);
        Assert.Equal(2, await ReadActiveOwnerCountAsync(connectionString, "default"));
        Assert.Equal(1, await ReadActiveGenerationCountAsync(connectionString, "default"));
    }
    [DockerAvailableFact]
    public async Task RuntimeResync_UpdatesEditedSeedWithoutRestart()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        using var host = CreateHost(
            connectionString, sourceDirectory, new CountingEmbeddingClient(), runtimeResyncEnabled: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            Path.Combine(sourceDirectory, "runbooks", "checkout-timeout.md"),
            "\nRuntime synchronization update.",
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(async () =>
            (await ReadSeedStateAsync(connectionString, "runbooks/checkout-timeout.md")).Version == 2);

        var status = host.Services.GetRequiredService<IMemorySeedSyncStatus>().Snapshot;
        Assert.True(status.RuntimeResyncEnabled);
        Assert.NotNull(status.LastSuccessAtUtc);
        Assert.NotNull(status.ActiveGeneration);
        Assert.Null(status.LastErrorCode);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [DockerAvailableFact]
    public async Task RuntimeResync_DeactivatesRemovedSeedWithoutRestart()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        using var host = CreateHost(
            connectionString, sourceDirectory, new CountingEmbeddingClient(), runtimeResyncEnabled: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(sourceDirectory, "incidents", "provider-unavailable.md"));
        await WaitUntilAsync(async () => (await ReadMemoryCountsAsync(connectionString)).Items == 1);

        var removed = await ReadSeedStateAsync(connectionString, "incidents/provider-unavailable.md");
        Assert.False(removed.IsActive);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [DockerAvailableFact]
    public async Task RuntimeResync_FailureIsVisibleWhilePreviousCorpusRemainsActive()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        using var host = CreateHost(
            connectionString, sourceDirectory, new FailAfterEmbeddingClient(2), runtimeResyncEnabled: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        var original = await ReadSeedStateAsync(connectionString, "runbooks/checkout-timeout.md");
        await File.AppendAllTextAsync(
            Path.Combine(sourceDirectory, "runbooks", "checkout-timeout.md"),
            "\nRuntime synchronization failure trigger.",
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() =>
            Task.FromResult(host.Services.GetRequiredService<IMemorySeedSyncStatus>()
                .Snapshot.LastErrorCode == "memory_sync_failed"));

        Assert.Equal(2, await ReadActiveOwnerCountAsync(connectionString, "default"));
        var current = await ReadSeedStateAsync(connectionString, "runbooks/checkout-timeout.md");
        Assert.Equal(original.Content, current.Content);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [DockerAvailableFact]
    public async Task StartupOnlyMode_DoesNotResyncEditedSeed()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        using var host = CreateHost(connectionString, sourceDirectory, new CountingEmbeddingClient());

        await host.StartAsync(TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            Path.Combine(sourceDirectory, "runbooks", "checkout-timeout.md"),
            "\nStartup-only update.",
            TestContext.Current.CancellationToken);

        // This proves an absence (no resync), so there is no positive condition to poll for.
        // MemorySeedHostedService.StartAsync only assigns resyncTask when RuntimeResyncEnabled,
        // so with it disabled no resync loop is ever scheduled - a long margin adds no additional
        // confidence over a short one. Keep a small fixed margin as defense-in-depth against a
        // future regression that re-enables scheduling without checking the flag.
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        var state = await ReadSeedStateAsync(connectionString, "runbooks/checkout-timeout.md");
        Assert.Equal(1, state.Version);
        Assert.False(host.Services.GetRequiredService<IMemorySeedSyncStatus>().Snapshot.RuntimeResyncEnabled);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }
    [DockerAvailableFact]
    public async Task RuntimeResync_StopCancelsInflightSyncWithoutFailureStatus()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        var embeddingClient = new BlockingAfterEmbeddingClient(2);
        using var host = CreateHost(
            connectionString, sourceDirectory, embeddingClient, runtimeResyncEnabled: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            Path.Combine(sourceDirectory, "runbooks", "checkout-timeout.md"),
            "\nCancellation trigger.",
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Task.FromResult(embeddingClient.CallCount >= 3));

        await host.StopAsync(TestContext.Current.CancellationToken);

        Assert.Null(host.Services.GetRequiredService<IMemorySeedSyncStatus>().Snapshot.LastErrorCode);
    }

    [DockerAvailableFact]
    public async Task StartAsync_RuntimeResyncRejectsOutOfRangeInterval()
    {
        var connectionString = await CreateSchemaAsync();
        var sourceDirectory = await CreateSeedDirectoryAsync();
        using var host = CreateHost(
            connectionString,
            sourceDirectory,
            new CountingEmbeddingClient(),
            runtimeResyncEnabled: true,
            runtimeResyncIntervalSeconds: 0);

        var exception = await Record.ExceptionAsync(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.IsType<InvalidOperationException>(exception);
    }

    [DockerAvailableFact]
    public async Task StartAsync_FrontmatterMetadataIsPersisted()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var sourceDirectory = await CreateSeedDirectoryAsync();
        using var host = CreateHost(connectionString, sourceDirectory, new CountingEmbeddingClient());

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        var state = await ReadSeedStateAsync(connectionString, "runbooks/checkout-timeout.md");
        Assert.Equal("runbook", state.Kind);
        Assert.Equal("checkout-api", state.ServiceName);
        Assert.Equal("payments", state.Component);
        Assert.Equal("0.1", state.ReleaseName);
        Assert.Contains("checkout", state.Tags);
    }

    [DockerAvailableFact]
    public async Task StartAsync_FailureStatusPersistenceDoesNotMaskSyncFailureOrStall()
    {
        var connectionString = await CreateSchemaAsync();
        await ClearMemoryAsync(connectionString);
        var statusWriter = new BlockingFailureStatusWriter();
        using var host = CreateHost(
            connectionString,
            await CreateSeedDirectoryAsync(),
            new FailAfterEmbeddingClient(0),
            configureServices: services =>
            {
                services.RemoveAll<IMemorySeedSyncStatusWriter>();
                services.AddSingleton<IMemorySeedSyncStatusWriter>(statusWriter);
            });

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));
        stopwatch.Stop();

        Assert.Equal("Simulated embedding failure.", exception.Message);
        Assert.True(statusWriter.FailureSaveStarted);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4));
    }
    private async Task<string> CreateSchemaAsync()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
        return connectionString;
    }

    private static IHost CreateHost(
        string connectionString,
        string sourceDirectory,
        IEmbeddingClient embeddingClient,
        string owner = "default",
        bool runtimeResyncEnabled = false,
        int runtimeResyncIntervalSeconds = 1,
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
                    ["IncidentCompass:Memory:Seed:Owner"] = owner,
                    ["IncidentCompass:Memory:Seed:RuntimeResyncEnabled"] = runtimeResyncEnabled.ToString(),
                    ["IncidentCompass:Memory:Seed:RuntimeResyncIntervalSeconds"] = runtimeResyncIntervalSeconds.ToString(CultureInfo.InvariantCulture),
                    ["IncidentCompass:Memory:Seed:SourceDirectory"] = sourceDirectory
                });
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddSingleton<IEmbeddingClient>(embeddingClient);
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
                services.AddEmbeddingHost(context.Configuration);
                configureServices?.Invoke(services);
            })
            .Build();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!await predicate())
        {
            await Task.Delay(50, timeout.Token);
        }
    }
    private static async Task<string> CreateSeedDirectoryAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "incidentcompass-memory-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "runbooks"));
        Directory.CreateDirectory(Path.Combine(directory, "incidents"));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "runbooks", "checkout-timeout.md"),
            """
            ---
            kind: Runbook
            service: checkout-api
            component: payments
            release: 0.1
            tags: [checkout, timeout]
            ---

            # Checkout Timeout Runbook

            Checkout timeout alerts usually indicate upstream payment latency.
            """,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "incidents", "provider-unavailable.md"),
            """
            ---
            kind: KnownIncident
            service: provider-client
            component: upstream
            release: 0.1
            tags: [provider, outage]
            ---

            # Provider Unavailable Incident

            ProviderUnavailableException bursts usually point to dependency outage.
            """,
            TestContext.Current.CancellationToken);
        return directory;
    }

    private static async Task ClearMemoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("DELETE FROM incidentcompass.memory_items;", connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<MemoryCounts> ReadMemoryCountsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT count(*) FROM incidentcompass.memory_items WHERE tenant_id = 'local' AND is_active = true),
                (SELECT count(*)
                   FROM incidentcompass.memory_chunks chunk
                   JOIN incidentcompass.memory_items item ON item.id = chunk.memory_item_id
                  WHERE chunk.tenant_id = 'local' AND item.is_active = true),
                (SELECT count(DISTINCT source)
                   FROM incidentcompass.memory_items
                  WHERE tenant_id = 'local' AND is_active = true);
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new MemoryCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<long> ReadActiveOwnerCountAsync(string connectionString, string owner)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM incidentcompass.memory_items
            WHERE tenant_id = 'local' AND seed_owner = @owner AND is_active = true;
            """, connection);
        command.Parameters.AddWithValue("owner", owner);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<long> ReadActiveGenerationCountAsync(string connectionString, string owner)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT count(DISTINCT seed_generation)
            FROM incidentcompass.memory_items
            WHERE tenant_id = 'local' AND seed_owner = @owner AND is_active = true;
            """, connection);
        command.Parameters.AddWithValue("owner", owner);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
    private static async Task<SeedState> ReadSeedStateAsync(string connectionString, string source)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT kind, content, version, service_name, component, release_name, tags, is_active
            FROM incidentcompass.memory_items
            WHERE tenant_id = 'local' AND source = @source AND seed_managed = true
            ORDER BY updated_at_utc DESC
            LIMIT 1;
            """, connection);
        command.Parameters.AddWithValue("source", source);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return new SeedState(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetFieldValue<string[]>(6),
            reader.GetBoolean(7));
    }

    private sealed class BlockingFailureStatusWriter : IMemorySeedSyncStatusWriter
    {
        public bool FailureSaveStarted { get; private set; }

        public Task SaveAsync(MemorySeedSyncSnapshot snapshot, CancellationToken cancellationToken)
        {
            if (snapshot.LastErrorCode != "memory_sync_failed")
            {
                return Task.CompletedTask;
            }

            FailureSaveStarted = true;
            return Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
        }
    }
    private sealed class CountingEmbeddingClient : IEmbeddingClient
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new EmbeddingResponse([1f, 0f], request.Model, "test", 1, request.CorrelationId));
        }
    }

    private sealed class FailAfterEmbeddingClient(int successfulCalls) : IEmbeddingClient
    {
        private int callCount;

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref callCount) > successfulCalls)
            {
                throw new InvalidOperationException("Simulated embedding failure.");
            }

            return Task.FromResult(new EmbeddingResponse([1f, 0f], request.Model, "test", 1, request.CorrelationId));
        }
    }
    private sealed class BlockingAfterEmbeddingClient(int successfulCalls) : IEmbeddingClient
    {
        private int callCount;

        public int CallCount => Volatile.Read(ref callCount);

        public async Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref callCount) <= successfulCalls)
            {
                return new EmbeddingResponse([1f, 0f], request.Model, "test", 1, request.CorrelationId);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Cancellation did not stop the embedding request.");
        }
    }

    private sealed record MemoryCounts(long Items, long Chunks, long Sources);

    private sealed record SeedState(
        string Kind,
        string Content,
        int Version,
        string? ServiceName,
        string? Component,
        string? ReleaseName,
        IReadOnlyList<string> Tags,
        bool IsActive);
}
