using System.Text;
using System.Text.Json;
using IncidentCompass.Application.Governance.ActionApprovals;

namespace IncidentCompass.Application.Remediation;

/// <summary>
/// The field readers every frozen remediation payload is parsed with.
/// </summary>
/// <remarks>
/// Two payload shapes are read back out of durable bytes - the one a <c>code_write</c> approval
/// freezes and the one a <c>branch_push</c> approval freezes - and both must refuse in exactly the
/// same way, because a field that is strict in one and loose in the other is a field an attacker only
/// has to find once. Sharing the readers is what makes "strict" a single definition rather than two
/// that drift.
/// </remarks>
internal static class RemediationPayloadFields
{
    /// <summary>Characters in a git object name.</summary>
    public const int GitObjectNameCharacters = 40;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static bool TryReadReportId(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            Guid.TryParseExact(property.GetString(), "N", out value) &&
            value != Guid.Empty &&
            string.Equals(property.GetString(), value.ToString("N"), StringComparison.Ordinal);
    }

    public static bool TryReadName(JsonElement root, string name, int maximumCharacters, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return !string.IsNullOrWhiteSpace(value) && value.Length <= maximumCharacters &&
            !value.Any(char.IsControl);
    }

    public static bool TryReadIdentity(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return ActionProposalValidator.IsLowerHexSha256(value);
    }

    public static bool TryReadGitObjectName(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return value.Length == GitObjectNameCharacters &&
            value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    public static bool TryReadCount(JsonElement root, string name, int minimum, int maximum, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value) && value >= minimum && value <= maximum;
    }

    public static int Utf8ByteCount(string value) => StrictUtf8.GetByteCount(value);

    public static byte[] Utf8Bytes(string value) => StrictUtf8.GetBytes(value);
}
