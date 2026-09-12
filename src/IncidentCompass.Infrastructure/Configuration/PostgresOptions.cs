namespace IncidentCompass.Infrastructure.Configuration;

public sealed class PostgresOptions
{
    public const string SectionName = "IncidentCompass:Postgres";

    public string ConnectionStringName { get; init; } = "IncidentCompass";

    /// <summary>
    /// Bounds the wait for the database to start listening. See
    /// <see cref="PostgresStartupRetryOptions"/> for why this is a startup-ordering budget rather
    /// than a steady-state retry policy.
    /// </summary>
    public PostgresStartupRetryOptions StartupRetry { get; init; } = new();
}
