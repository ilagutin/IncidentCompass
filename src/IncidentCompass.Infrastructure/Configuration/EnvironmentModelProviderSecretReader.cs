namespace IncidentCompass.Infrastructure.Configuration;

/// <summary>
/// Resolves a provider entry's <c>ApiKeySecretRef</c> against the process environment.
/// <para>
/// This mirrors <see cref="Intake.EnvironmentPlaceholderExpander" />, which already resolves
/// <c>${VAR}</c> placeholders in the same configuration file from the same source. The difference is
/// deliberate: a placeholder is substituted into the loaded document, and the loaded document is
/// hashed and persisted as a triage-configuration snapshot, so a credential must never be written
/// as one. <c>ApiKeySecretRef</c> keeps the variable's name in the snapshot and resolves the value
/// here, outside anything that is stored.
/// </para>
/// </summary>
internal sealed class EnvironmentModelProviderSecretReader : IModelProviderSecretReader
{
    public string? Read(string secretRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretRef);
        return Environment.GetEnvironmentVariable(secretRef);
    }
}
