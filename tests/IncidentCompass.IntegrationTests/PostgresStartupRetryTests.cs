using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using IncidentCompass.Application.Core.Exceptions;
using IncidentCompass.Infrastructure.Configuration;
using IncidentCompass.Infrastructure.Postgres;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IncidentCompass.IntegrationTests;

/// <summary>
/// Proves the startup connection budget against a real socket rather than a mocked failure: a host
/// that starts before its database must survive the refusal, and a host whose database never
/// appears must still fail loudly with the normalized persistence error.
/// </summary>
[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class PostgresStartupRetryTests(PostgresRepositoryFixture postgres)
{
    [DockerAvailableFact]
    public async Task OpenConnectionAsync_SucceedsWhenTheDatabaseStartsListeningLate()
    {
        var containerConnectionString = await postgres.GetConnectionStringAsync();
        var container = new NpgsqlConnectionStringBuilder(containerConnectionString);
        var refusedPort = ReserveFreeLoopbackPort();
        var provider = CreateProvider(
            RedirectedTo(container, refusedPort),
            maxAttempts: 100,
            initialDelayMs: 50,
            maxDelayMs: 200,
            maxTotalDurationSeconds: 30);

        LoopbackTcpForwarder? forwarder = null;
        try
        {
            var opening = provider.OpenConnectionAsync(TestContext.Current.CancellationToken);

            // Nothing is listening on the reserved port yet, so the first attempts are refused.
            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
            forwarder = new LoopbackTcpForwarder(refusedPort, container.Host!, container.Port);

            await using var connection = await opening;
            await using var command = new NpgsqlCommand("SELECT 1;", connection);
            var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, Assert.IsType<int>(value));
        }
        finally
        {
            if (forwarder is not null)
            {
                await forwarder.DisposeAsync();
            }

            provider.Dispose();
        }
    }

    [Fact]
    public async Task OpenConnectionAsync_FailsWithNormalizedPersistenceErrorWhenDatabaseNeverAppears()
    {
        var refusedPort = ReserveFreeLoopbackPort();
        var options = new PostgresConnectionOptions
        {
            ConnectionStringName = "IncidentCompass",
            ConnectionString =
                $"Host=127.0.0.1;Port={refusedPort};Database=incidentcompass;" +
                "Username=incidentcompass;Password=unused;Timeout=2"
        };
        var log = new RecordingLogger();
        using var provider = CreateProvider(
            options,
            maxAttempts: 3,
            initialDelayMs: 10,
            maxDelayMs: 20,
            // The duration bound plays no part in this test's intent, and a small one would take it
            // over on a starved CI runner where each refused connect is slow: the loop would then
            // end on expiry at attempt 2 rather than on the attempt count at 3. CI is Linux.
            maxTotalDurationSeconds: 60,
            logger: log);

        var elapsed = Stopwatch.StartNew();
        var failure = await Assert.ThrowsAsync<PersistenceException>(
            () => provider.OpenConnectionAsync(TestContext.Current.CancellationToken));
        elapsed.Stop();

        // The port boundary normalized the provider failure: no raw Npgsql type escaped it, and the
        // provider detail survives as the inner exception.
        Assert.Equal("open PostgreSQL connection", failure.Operation);
        Assert.IsAssignableFrom<NpgsqlException>(failure.InnerException);

        // Two waits happened before the third and final attempt, so the budget really was spent
        // rather than the first refusal being reported straight away.
        Assert.Equal(2, log.Entries.Count(entry => entry == LogLevel.Warning));
        Assert.Equal(1, log.Entries.Count(entry => entry == LogLevel.Error));
        Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(20), $"elapsed {elapsed.Elapsed}");
    }

    private static PostgresConnectionOptions RedirectedTo(
        NpgsqlConnectionStringBuilder container,
        int port)
    {
        var redirected = new NpgsqlConnectionStringBuilder(container.ConnectionString)
        {
            Host = "127.0.0.1",
            Port = port
        };

        return new PostgresConnectionOptions
        {
            ConnectionStringName = "IncidentCompass",
            ConnectionString = redirected.ConnectionString
        };
    }

    private static PostgresDataSourceProvider CreateProvider(
        PostgresConnectionOptions connectionOptions,
        int maxAttempts,
        int initialDelayMs,
        int maxDelayMs,
        int maxTotalDurationSeconds,
        ILogger<PostgresFirstConnectionRetry>? logger = null)
    {
        var postgresOptions = Options.Create(new PostgresOptions
        {
            StartupRetry = new PostgresStartupRetryOptions
            {
                MaxAttempts = maxAttempts,
                InitialDelayMilliseconds = initialDelayMs,
                MaxDelayMilliseconds = maxDelayMs,
                MaxTotalDurationSeconds = maxTotalDurationSeconds
            }
        });

        return new PostgresDataSourceProvider(
            Options.Create(connectionOptions),
            new PostgresFirstConnectionRetry(postgresOptions, TimeProvider.System, logger));
    }

    /// <summary>
    /// Takes a loopback port from the operating system and immediately gives it back, so the port
    /// is known and refuses connections until the test decides to listen on it.
    /// </summary>
    private static int ReserveFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// A transparent byte forwarder that stands in for "the database has finished starting". It is
    /// how the test makes a previously refused port begin answering without restarting the shared
    /// container, which is the exact transition the startup budget exists for.
    /// </summary>
    private sealed class LoopbackTcpForwarder : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource shutdown = new();
        private readonly Task acceptLoop;

        public LoopbackTcpForwarder(int listenPort, string targetHost, int targetPort)
        {
            listener = new TcpListener(IPAddress.Loopback, listenPort);
            listener.Start();
            acceptLoop = AcceptAsync(targetHost, targetPort, shutdown.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await shutdown.CancelAsync();
            listener.Stop();

            // Awaited only to observe it: an abandoned faulted task surfaces later as a
            // process-level UnobservedTaskException.
            try
            {
                await acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }

            shutdown.Dispose();
        }

        private async Task AcceptAsync(string targetHost, int targetPort, CancellationToken token)
        {
            var connections = new List<Task>();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(token);
                    connections.Add(ForwardAsync(client, targetHost, targetPort, token));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            await Task.WhenAll(connections);
        }

        private static async Task ForwardAsync(
            TcpClient client,
            string targetHost,
            int targetPort,
            CancellationToken token)
        {
            using (client)
            using (var upstream = new TcpClient())
            {
                try
                {
                    await upstream.ConnectAsync(targetHost, targetPort, token);
                    var downstreamStream = client.GetStream();
                    var upstreamStream = upstream.GetStream();

                    // Either half closing ends the pair; the sockets are torn down by the usings.
                    await Task.WhenAny(
                        downstreamStream.CopyToAsync(upstreamStream, token),
                        upstreamStream.CopyToAsync(downstreamStream, token));
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                }
            }
        }
    }

    /// <summary>
    /// Records only the level of each entry. The retry logs an exception type name and nothing
    /// else, so there is no message text worth capturing and none worth asserting on.
    /// </summary>
    private sealed class RecordingLogger : ILogger<PostgresFirstConnectionRetry>
    {
        public List<LogLevel> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add(logLevel);
            }
        }
    }
}
