using System.Net;
using System.Text.Json;
using IncidentCompass.Application.Remediation;
using IncidentCompass.Infrastructure.Tickets;

namespace IncidentCompass.Infrastructure.Remediation;

/// <summary>
/// Sends one bounded Git Data request and returns a parsed document or a closed code.
/// </summary>
/// <remarks>
/// Every request goes through here so that the bounds are stated once: no redirect, a per-call
/// deadline linked to the caller's token rather than a client-wide timeout, a response body read
/// through a size bound, and a narrow catch that turns a transport or decoding fault into a code
/// instead of an exception. Nothing a provider says - a message, a header, a URL - escapes into the
/// code that comes back.
/// </remarks>
internal sealed class GitHubGitDataSender(HttpClient client, int timeoutSeconds)
{
    public async Task<(JsonDocument? Document, string? Code)> SendAsync(
        Func<HttpRequestMessage> factory,
        int maximumResponseBytes,
        CancellationToken cancellationToken,
        string? absentCode = null)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var request = factory();
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (absentCode is not null && response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, absentCode);
            }

            if (GitHubGitDataParser.MapFailure(response.StatusCode) is { } failure)
            {
                return (null, failure);
            }

            var bytes = await BoundedHttpContentReader.ReadAsync(
                response.Content, maximumResponseBytes, deadline.Token);
            return bytes is null
                ? (null, CodePublicationCodes.ResponseMalformed)
                : (JsonDocument.Parse(bytes), null);
        }
        catch (JsonException)
        {
            return (null, CodePublicationCodes.ResponseMalformed);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or HttpRequestException or IOException)
        {
            return (null, CodePublicationCodes.Unavailable);
        }
    }

    /// <summary>
    /// Sends the one request whose outcome matters if it is repeated, returning only its status.
    /// </summary>
    /// <remarks>
    /// It is separate from <see cref="SendAsync" /> because its failure mapping differs in the one way
    /// that matters: a transport fault after a reference create is not "unavailable, try later" but
    /// "the outcome is not known", which is a durable state a person settles.
    /// </remarks>
    public async Task<HttpStatusCode?> SendCreateAsync(
        Func<HttpRequestMessage> factory,
        CancellationToken cancellationToken)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var request = factory();
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return response.StatusCode;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or HttpRequestException or IOException)
        {
            return null;
        }
    }
}
