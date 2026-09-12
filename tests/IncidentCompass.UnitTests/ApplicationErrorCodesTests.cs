using System.Reflection;
using IncidentCompass.Application.Core.Exceptions;

namespace IncidentCompass.UnitTests;

/// <summary>
/// The codes are the machine-readable half of the public API error contract: a caller branches on
/// the string, so respelling one is a breaking change even though nothing in the compiler notices.
/// Several codes are produced at a single throw site each and were asserted nowhere, which left
/// their spelling free to drift. These tests pin the whole set rather than the reachable few.
/// </summary>
public sealed class ApplicationErrorCodesTests
{
    private static readonly Dictionary<string, string> ExpectedCodes = new(StringComparer.Ordinal)
    {
        ["FaultNotFound"] = "fault_not_found",
        ["TriageReportNotFound"] = "triage_report_not_found",
        ["ActionApprovalNotFound"] = "action_approval_not_found",
        ["TriageConfigurationSnapshotNotFound"] = "triage_configuration_snapshot_not_found",
        ["ActionProposalProvenanceConflict"] = "action_proposal_provenance_conflict",
        ["ActionProposalInputConflict"] = "action_proposal_input_conflict",
        ["ActionApprovalConflictCodePrefix"] = "action_approval_conflict_",
        ["TenantContextRequired"] = "tenant_context_required",
        ["ActionOperatorRequired"] = "action_operator_required"
    };

    [Fact]
    public void DeclaredCodes_MatchThePublishedWireValues()
    {
        var declared = ReadDeclaredCodes()
            .Select(code => $"{code.Name}={code.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expected = ExpectedCodes
            .Select(entry => $"{entry.Key}={entry.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, declared);
    }

    [Fact]
    public void DeclaredCodes_AreUniqueLowerSnakeCaseTokens()
    {
        var declared = ReadDeclaredCodes();
        var malformed = declared
            .Where(code => !HasTokenShape(code.Name, code.Value))
            .Select(code => $"{code.Name}={code.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        var duplicates = declared
            .GroupBy(code => code.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Empty(malformed);
        Assert.Empty(duplicates);
    }

    private static (string Name, string Value)[] ReadDeclaredCodes() =>
        typeof(ApplicationErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (field.Name, Value: (string)field.GetRawConstantValue()!))
            .ToArray();

    /// <summary>
    /// A code is a lowercase ASCII snake_case token. A constant whose name ends in
    /// <c>CodePrefix</c> is concatenated with a suffix at the throw site, so it is the one shape
    /// that must end with the separator instead of a character.
    /// </summary>
    private static bool HasTokenShape(string name, string value)
    {
        if (value.Length == 0 || value[0] == '_')
        {
            return false;
        }

        var isPrefix = name.EndsWith("CodePrefix", StringComparison.Ordinal);
        if (isPrefix != (value[^1] == '_'))
        {
            return false;
        }

        return value.All(character =>
            character == '_' || char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character));
    }
}
