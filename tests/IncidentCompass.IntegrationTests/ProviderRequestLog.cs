namespace IncidentCompass.IntegrationTests;

/// <summary>
/// What one fake provider endpoint actually received. Routing is asserted from here rather than
/// from the client, because the question these tests answer is which server got the request and
/// which credential it was handed, and only the server can answer that.
/// </summary>
internal sealed class ProviderRequestLog
{
    private readonly List<string> authorizations = [];
    private readonly Lock gate = new();

    public IReadOnlyList<string> Authorizations
    {
        get
        {
            lock (gate)
            {
                return authorizations.ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                return authorizations.Count;
            }
        }
    }

    public void Record(string authorization)
    {
        lock (gate)
        {
            authorizations.Add(authorization);
        }
    }
}
