using System.Reflection;
using IncidentCompass.Application.Investigation.Jobs;

namespace IncidentCompass.UnitTests;

/// <summary>
/// Keeps the validator's supported-schema-type set and the type switch in <c>ValidateElement</c>
/// describing the same set of types.
/// </summary>
/// <remarks>
/// Driving the switch off the set would close the gap outright, but that is private state of a
/// validator this change does not otherwise touch, so the agreement is asserted from the outside
/// instead. The set is read by reflection rather than restated here, so the assertion covers every
/// member the validator actually declares instead of a third hand-maintained list.
/// </remarks>
public sealed class AnalysisWorkerOutputSchemaValidatorTests
{
    /// <summary>
    /// Every type token a JSON Schema can declare, plus one token no schema declares. The last one
    /// keeps the probe honest: if it stopped detecting the unsupported-type failure and reported
    /// every token as evaluated, that token alone would disagree with the set and fail the test.
    /// </summary>
    private static readonly string[] JsonSchemaTypeTokens =
    [
        "object",
        "array",
        "string",
        "boolean",
        "number",
        "integer",
        "null",
        "no_such_schema_type"
    ];

    [Fact]
    public void SupportedSchemaTypeSetMatchesTheTypesValidationCanEvaluate()
    {
        var declaredTypes = ReadSupportedSchemaTypes();
        var candidates = declaredTypes
            .Union(JsonSchemaTypeTokens, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var disagreeing = candidates
            .Where(schemaType =>
                AnalysisWorkerOutputSchemaValidator.IsSupportedSchemaType(schemaType) !=
                IsEvaluatedByValidation(schemaType))
            .ToArray();

        Assert.NotEmpty(declaredTypes);
        Assert.Empty(disagreeing);
    }

    /// <summary>
    /// Whether <c>ValidateElement</c> has an arm for the type. A supported type either accepts the
    /// output or reports correctable violations; an unsupported one leaves the worker loop as a bare
    /// <see cref="InvalidOperationException" />. <c>WorkerOutputValidationException</c> derives from
    /// that exception, so the correctable failure has to be caught first.
    /// </summary>
    private static bool IsEvaluatedByValidation(string schemaType)
    {
        var schema = "{\"type\":\"" + schemaType + "\"}";
        try
        {
            AnalysisWorkerOutputSchemaValidator.Validate("{}", schema, "analysis");
            return true;
        }
        catch (WorkerOutputValidationException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyCollection<string> ReadSupportedSchemaTypes()
    {
        var field = typeof(AnalysisWorkerOutputSchemaValidator)
            .GetField("SupportedSchemaTypes", BindingFlags.NonPublic | BindingFlags.Static);
        if (field is null)
        {
            throw new InvalidOperationException(
                "AnalysisWorkerOutputSchemaValidator.SupportedSchemaTypes was renamed or removed.");
        }

        return (IReadOnlyCollection<string>)field.GetValue(null)!;
    }
}
