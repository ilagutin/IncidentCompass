namespace IncidentCompass.Infrastructure.Postgres;

internal readonly record struct PostgresMigrationChecksum(string Value)
{
    public override string ToString() => Value;
}
