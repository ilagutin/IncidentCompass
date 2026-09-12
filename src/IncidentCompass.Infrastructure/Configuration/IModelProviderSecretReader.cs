namespace IncidentCompass.Infrastructure.Configuration;

/// <summary>
/// Reads the value a triage-configuration provider entry's <c>ApiKeySecretRef</c> names.
/// <para>
/// The tracked triage configuration carries only the name of the place a credential lives, never
/// the credential. This is the one seam that turns that name into a value, so a test can supply a
/// fake without setting process-wide environment state, and so there is a single place to look when
/// asking where a provider credential came from. It is deliberately not a secret store: the only
/// implementation reads the process environment, which is what
/// <c>docs/security-model.md</c> already says credentials arrive through.
/// </para>
/// </summary>
internal interface IModelProviderSecretReader
{
    /// <summary>
    /// Returns the value bound to <paramref name="secretRef" />, or <see langword="null" /> when
    /// nothing is bound to that name. Implementations must not log, echo or otherwise emit the
    /// value they return.
    /// </summary>
    string? Read(string secretRef);
}
