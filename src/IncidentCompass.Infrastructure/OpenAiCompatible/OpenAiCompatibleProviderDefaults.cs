namespace IncidentCompass.Infrastructure.OpenAiCompatible;

/// <summary>
/// The host-wide OpenAI-compatible profile a provider entry falls back to, plus the transport
/// settings that stay host-wide whichever provider answers a call.
/// <para>
/// <see cref="EndpointPath" /> and <see cref="AllowInsecureHttpForLoopback" /> are not per-provider.
/// The path is the OpenAI-compatible shape itself, which is what "OpenAI-compatible" means, and the
/// loopback allowance is a host security posture: letting a provider entry in tracked configuration
/// opt itself into plaintext HTTP would move that decision out of the operator's hands.
/// </para>
/// <para>
/// A class rather than a record, for the reason given on
/// <see cref="OpenAiCompatibleProviderProfile" />: it carries a credential.
/// </para>
/// </summary>
internal sealed class OpenAiCompatibleProviderDefaults(
    string baseUrl,
    string? apiKey,
    string endpointPath,
    bool allowInsecureHttpForLoopback)
{
    public string BaseUrl { get; } = baseUrl;

    public string? ApiKey { get; } = apiKey;

    public string EndpointPath { get; } = endpointPath;

    public bool AllowInsecureHttpForLoopback { get; } = allowInsecureHttpForLoopback;

    public override string ToString() => nameof(OpenAiCompatibleProviderDefaults);
}
