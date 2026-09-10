namespace IncidentCompass.Infrastructure.OpenAiCompatible;

/// <summary>
/// One resolved OpenAI-compatible provider: the absolute endpoint a call goes to and the credential
/// it authenticates with.
/// <para>
/// This is a class and not a record on purpose. A record's generated <c>ToString</c> prints every
/// member, so the moment this type were a record, interpolating it into a message, an exception or
/// a structured log argument anywhere in the process would print the credential. A plain class
/// prints its type name, and <see cref="ToString" /> is overridden anyway so the safe answer is
/// stated rather than inherited.
/// </para>
/// </summary>
internal sealed class OpenAiCompatibleProviderProfile(Uri endpointUri, string apiKey)
{
    public Uri EndpointUri { get; } = endpointUri;

    public string ApiKey { get; } = apiKey;

    public override string ToString() => nameof(OpenAiCompatibleProviderProfile);
}
