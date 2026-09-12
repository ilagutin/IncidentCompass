using IncidentCompass.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace IncidentCompass.Infrastructure.Postgres;

internal sealed class PostgresDataSourceProvider : IDisposable
{
    private readonly IOptions<PostgresConnectionOptions> options;
    private readonly PostgresFirstConnectionRetry firstConnectionRetry;
    private readonly Lazy<NpgsqlDataSource> dataSource;

    public PostgresDataSourceProvider(
        IOptions<PostgresConnectionOptions> options,
        PostgresFirstConnectionRetry firstConnectionRetry)
    {
        this.options = options;
        this.firstConnectionRetry = firstConnectionRetry;
        dataSource = new Lazy<NpgsqlDataSource>(
            CreateDataSource,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        PostgresOperation.ExecuteAsync(
            "open PostgreSQL connection",
            () => OpenConnectionCoreAsync(cancellationToken));

    /// <summary>
    /// The startup wait sits inside <see cref="PostgresOperation"/>, not around it, so an exhausted
    /// budget leaves through the same normalization every other persistence failure does and
    /// surfaces as the port contract's exception rather than a raw provider type. Data-source
    /// creation runs inside the retry delegate, so a bad connection string is thrown there like any
    /// other failure; what stops it being retried is the retry's own predicate, which treats a
    /// configuration error as permanent and rethrows it on the first attempt.
    /// </summary>
    private Task<NpgsqlConnection> OpenConnectionCoreAsync(CancellationToken cancellationToken) =>
        firstConnectionRetry.OpenAsync(
            token => dataSource.Value.OpenConnectionAsync(token).AsTask(),
            cancellationToken);

    public void Dispose()
    {
        if (dataSource.IsValueCreated)
        {
            dataSource.Value.Dispose();
        }
    }

    private NpgsqlDataSource CreateDataSource()
    {
        var connectionOptions = options.Value;
        var connectionStringName = connectionOptions.ConnectionStringName;
        if (string.IsNullOrWhiteSpace(connectionOptions.ConnectionString))
        {
            throw PostgresConnectionConfigurationException.Missing(connectionStringName);
        }

        try
        {
            var builder = new NpgsqlDataSourceBuilder(connectionOptions.ConnectionString);
            builder.UseVector();
            return builder.Build();
        }
        catch (Exception exception) when (IsConnectionConfigurationException(exception))
        {
            throw PostgresConnectionConfigurationException.Invalid(connectionStringName);
        }
    }

    private static bool IsConnectionConfigurationException(Exception exception)
    {
        return exception is ArgumentException or InvalidOperationException or NotSupportedException;
    }
}
