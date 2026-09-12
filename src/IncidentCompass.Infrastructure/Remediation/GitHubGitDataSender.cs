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
    /// <summary>
    /// Sends one bounded request and returns its parsed body or a closed code.
    /// </summary>
    /// <remarks>
    /// <c>unknownOnFault</c> says whether a transport, decoding or server-side fault means the outcome
    /// is not known rather than that the provider was unavailable. It is set by the one caller that
    /// sends a request which may have created something before the answer was lost; every read passes
    /// it false, because a read that failed changed nothing and saying otherwise would manufacture
    /// in-doubt rows out of timeouts. A rejection the provider stated is never an unknown outcome.
    /// </remarks>
    public async Task<(JsonDocument? Document, string? Code)> SendAsync(
        Func<HttpRequestMessage> factory,
        int maximumResponseBytes,
        CancellationToken cancellationToken,
        string? absentCode = null,
        bool unknownOnFault = false)
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
                // A rejection the provider stated is a rejection: nothing was created, whatever the
                // request was. Only a server-side failure after a write leaves the outcome open.
                return (null, unknownOnFault && (int)response.StatusCode >= 500
                    ? CodePublicationCodes.OutcomeUnknown
                    : failure);
            }

            var bytes = await BoundedHttpContentReader.ReadAsync(
                response.Content, maximumResponseBytes, deadline.Token);
            return bytes is null
                ? (null, Fault(unknownOnFault, CodePublicationCodes.ResponseMalformed))
                : (JsonDocument.Parse(bytes), null);
        }
        catch (JsonException)
        {
            return (null, Fault(unknownOnFault, CodePublicationCodes.ResponseMalformed));
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or HttpRequestException or IOException)
        {
            return (null, Fault(unknownOnFault, CodePublicationCodes.Unavailable));
        }
    }

    private static string Fault(bool unknownOnFault, string code) =>
        unknownOnFault ? CodePublicationCodes.OutcomeUnknown : code;

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
