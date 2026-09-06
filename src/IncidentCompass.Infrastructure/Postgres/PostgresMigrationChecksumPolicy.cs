using System.Security.Cryptography;
using System.Text;

namespace IncidentCompass.Infrastructure.Postgres;

internal sealed class PostgresMigrationChecksumPolicy
{
    private readonly PostgresMigrationChecksum? legacyCrlfChecksum;

    private PostgresMigrationChecksumPolicy(
        int version,
        string name,
        PostgresMigrationChecksum canonicalChecksum,
        PostgresMigrationChecksum? legacyCrlfChecksum)
    {
        Version = version;
        Name = name;
        CanonicalChecksum = canonicalChecksum;
        this.legacyCrlfChecksum = legacyCrlfChecksum;
    }

    public int Version { get; }

    public string Name { get; }

    public PostgresMigrationChecksum CanonicalChecksum { get; }

    internal PostgresMigrationChecksum? ReleasedLegacyCrlfChecksum => legacyCrlfChecksum;

    public bool Accepts(int durableVersion, string durableName, string durableChecksum)
    {
        if (durableVersion != Version ||
            !string.Equals(durableName, Name, StringComparison.Ordinal))
        {
            return false;
        }

        var checksum = new PostgresMigrationChecksum(durableChecksum);
        return checksum == CanonicalChecksum || checksum == legacyCrlfChecksum;
    }

    public static async Task<PostgresMigrationChecksumPolicy> CreateAsync(
        PostgresSchemaMigration migration,
        CancellationToken cancellationToken)
    {
        var scripts = new List<KeyValuePair<string, string>>(migration.ScriptNames.Count);
        foreach (var scriptName in migration.ScriptNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scripts.Add(new(
                scriptName,
                await PostgresMigrationScriptExecutor.ReadAsync(scriptName, cancellationToken)));
        }

        return Create(
            migration.Version,
            migration.Name,
            scripts,
            migration.AcceptsReleasedLegacyChecksums);
    }

    internal static PostgresMigrationChecksumPolicy Create(
        int version,
        string name,
        IReadOnlyList<KeyValuePair<string, string>> scripts,
        bool acceptsReleasedLegacyChecksums)
    {
        var canonicalScripts = scripts
            .Select(script => new KeyValuePair<string, string>(
                script.Key,
                CanonicalizeSql(script.Value)))
            .ToArray();
        var canonicalChecksum = Compute(canonicalScripts);
        var legacyCrlfChecksum = acceptsReleasedLegacyChecksums
            ? Compute(canonicalScripts.Select(script => new KeyValuePair<string, string>(
                script.Key,
                script.Value.Replace("\n", "\r\n", StringComparison.Ordinal))))
            : (PostgresMigrationChecksum?)null;

        return new(version, name, canonicalChecksum, legacyCrlfChecksum);
    }

    private static string CanonicalizeSql(string sql)
    {
        var start = sql.Length > 0 && sql[0] == '\uFEFF' ? 1 : 0;
        var canonical = new StringBuilder(sql.Length - start);
        for (var index = start; index < sql.Length; index++)
        {
            var character = sql[index];
            if (character != '\r')
            {
                canonical.Append(character);
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == '\n')
            {
                index++;
            }

            canonical.Append('\n');
        }

        return canonical.ToString();
    }

    private static PostgresMigrationChecksum Compute(
        IEnumerable<KeyValuePair<string, string>> scripts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var script in scripts)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(script.Key));
            hash.AppendData([0]);
            hash.AppendData(Encoding.UTF8.GetBytes(script.Value));
            hash.AppendData([0]);
        }

        return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }
}
