using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IncidentCompass.Application.Core.Serialization;

namespace IncidentCompass.Infrastructure.Tickets;

internal static partial class GitHubIssueCommentMarker
{
    /// <summary>
    /// The payload shape this reader admits. Version 2 carries one more property than version 1: the
    /// pull request a backlink points at, null on a plain governed comment. See
    /// <see cref="GitHubIssueCommentPayload" /> for what changing it cost.
    /// </summary>
    public const int SchemaVersion = 2;

    /// <summary>Properties a payload must have, exactly: no fewer, and no sixth of someone's choosing.</summary>
    public const int PropertyCount = 6;

    /// <summary>Maximum UTF-8 bytes in a comment body.</summary>
    public const int MaximumBodyBytes = 4096;

    /// <summary>Largest pull-request number a comment will carry, in the provider's own shape.</summary>
    public const int MaximumPullRequestNumber = 1_000_000_000;

    private const string Prefix = "<!-- incidentcompass-ticket-comment:";
    private const string Suffix = " -->";
    private const int MaximumPayloadBytes = 8192;

    /// <summary>
    /// The at-most-once marker for one comment. It covers the proposal key, which already carries the
    /// tool id, so a governed comment and a backlink on the same ticket have different markers and
    /// neither suppresses the other; and it covers the ticket, so one report leaves at most one comment
    /// of each kind on each ticket.
    /// </summary>
    public static string Create(string proposalKey, Guid originReportId, string ticketId)
    {
        var value = Encoding.UTF8.GetBytes(
            proposalKey + "\n" + originReportId.ToString("N") + "\n" + ticketId);
        return Convert.ToHexStringLower(SHA256.HashData(value));
    }

    public static string Comment(string marker) => Prefix + marker + Suffix;

    public static bool IsValid(string? marker) =>
        marker is not null && MarkerPattern().IsMatch(marker);

    /// <summary>
    /// Whether a value is exactly the provider's own spelling of a positive number, small enough to be
    /// one of its identifiers. Used for both the ticket and the pull request, so that neither can be
    /// padded, signed or written in another base and still be accepted.
    /// </summary>
    public static bool TryReadNumber(string? value, int maximum, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
        number > 0 && number <= maximum &&
        string.Equals(value, number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    public static bool TryReadPayload(
        ReadOnlyMemory<byte> payload,
        out GitHubIssueCommentPayload value)
    {
        value = null!;
        if (payload.Length is < 1 or > MaximumPayloadBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != PropertyCount ||
                !root.TryGetProperty("schemaVersion", out var version) ||
                !version.TryGetInt32(out var schemaVersion) || schemaVersion != SchemaVersion ||
                !root.TryGetProperty("originReportId", out var report) ||
                report.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(report.GetString(), "N", out var reportId) ||
                !root.TryGetProperty("ticketId", out var ticket) ||
                ticket.ValueKind != JsonValueKind.String ||
                !TryReadNumber(ticket.GetString(), int.MaxValue, out var issueNumber) ||
                !TryReadPullRequest(root, out var pullRequest) ||
                !root.TryGetProperty("marker", out var markerNode) ||
                markerNode.ValueKind != JsonValueKind.String || !IsValid(markerNode.GetString()) ||
                !root.TryGetProperty("body", out var bodyNode) || bodyNode.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var marker = markerNode.GetString()!;
            var body = bodyNode.GetString()!;
            if (string.IsNullOrWhiteSpace(body) ||
                Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes ||
                !body.EndsWith(Comment(marker), StringComparison.Ordinal))
            {
                return false;
            }

            var canonical = Encoding.UTF8.GetBytes(
                CanonicalJsonSerializer.Canonicalize(JsonNode.Parse(payload.Span)));
            if (!payload.Span.SequenceEqual(canonical))
            {
                return false;
            }

            value = new GitHubIssueCommentPayload(
                reportId,
                issueNumber.ToString(CultureInfo.InvariantCulture),
                issueNumber,
                marker,
                body,
                pullRequest);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The one new field. It is present on every payload and is either JSON null or the provider's own
    /// spelling of a number; there is no third form, so "no pull request" cannot be written as an empty
    /// string, a zero or a missing property.
    /// </summary>
    private static bool TryReadPullRequest(JsonElement root, out string? value)
    {
        value = null;
        if (!root.TryGetProperty("pullRequestNumber", out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String ||
            !TryReadNumber(property.GetString(), MaximumPullRequestNumber, out _))
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();
}
