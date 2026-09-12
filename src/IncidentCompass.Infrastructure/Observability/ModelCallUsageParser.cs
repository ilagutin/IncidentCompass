using System.Text;
using System.Text.Json;

namespace IncidentCompass.Infrastructure.Observability;

/// <summary>
/// Reads the <c>ModelCallLedgerMetadata</c> payload of one durable <c>ModelCall</c> ledger row.
/// </summary>
/// <remarks>
/// The property names are duplicated here rather than shared with the writing record, which is the
/// convention this reader already followed: the row is a persisted JSON contract and a reader of it
/// must keep working against rows written by an older writer. Anything the reader cannot understand
/// fails closed, so an unreadable row costs its tokens and its spend rather than being guessed at.
/// </remarks>
internal static class ModelCallUsageParser
{
    private const int MaximumMetadataBytes = 8192;
    private const int MaximumIdentityBytes = 256;

    public static bool TryParse(string? rationale, out ModelCallUsage? usage)
    {
        usage = null;
        if (string.IsNullOrEmpty(rationale) ||
            rationale.Length > MaximumMetadataBytes ||
            Encoding.UTF8.GetByteCount(rationale) > MaximumMetadataBytes)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rationale, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateProperties(root) ||
                !TryReadIdentity(root, "provider", out var provider) ||
                !TryReadIdentity(root, "model", out var model) ||
                !TryReadIdentity(root, "usageSource", out var usageSource) ||
                !TryReadOptionalIdentity(root, "providerId", out var providerId) ||
                !TryReadTokenCount(root, "inputTokens", out var inputTokens) ||
                !TryReadTokenCount(root, "outputTokens", out var outputTokens) ||
                !TryReadTokenCount(root, "totalTokens", out var totalTokens))
            {
                return false;
            }

            usage = new ModelCallUsage(
                provider!,
                providerId,
                model!,
                usageSource!,
                inputTokens,
                outputTokens,
                totalTokens);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasDuplicateProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return root.EnumerateObject().Any(property => !names.Add(property.Name));
    }

    private static bool TryReadIdentity(
        JsonElement root,
        string propertyName,
        out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null &&
               value.Length > 0 &&
               string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
               Encoding.UTF8.GetByteCount(value) <= MaximumIdentityBytes;
    }

    /// <summary>
    /// Reads an identity a row is allowed not to carry, such as the configured provider id a row
    /// written before that field existed cannot have.
    /// </summary>
    /// <remarks>
    /// Absent succeeds with <see langword="null"/>; present but not a usable identity fails, so a
    /// blank or padded value is refused instead of quietly becoming a payer name.
    /// </remarks>
    private static bool TryReadOptionalIdentity(
        JsonElement root,
        string propertyName,
        out string? value)
    {
        value = null;
        return !root.TryGetProperty(propertyName, out _) || TryReadIdentity(root, propertyName, out value);
    }

    private static bool TryReadTokenCount(
        JsonElement root,
        string propertyName,
        out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value) &&
               value >= 0;
    }
}
