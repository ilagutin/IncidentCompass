using IncidentCompass.Application.Investigation.Jobs;

namespace IncidentCompass.UnitTests;

public sealed class WorkerOutputDiagnosticsTests
{
    private const string ModelControlledPropertyName = "model-secret-property";

    [Fact]
    public void SchemaValidator_UsesRoleNameInDiagnostics()
    {
        const string schema = """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "matched": { "type": "boolean" },
                "items": { "type": "array", "items": { "type": "object" } }
              },
              "required": ["matched", "items"]
            }
            """;

        var exception = Assert.Throws<WorkerOutputValidationException>(() =>
            AnalysisWorkerOutputSchemaValidator.Validate("{}", schema, "memory"));

        Assert.Contains("memory worker output", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Analysis worker output", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaValidator_ReportsAllViolationsWithoutModelControlledPropertyNames()
    {
        const string schema = """
            {
              "type": "object",
              "additionalProperties": false,
              "properties": {
                "matched": { "type": "boolean" },
                "items": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": { "known": { "type": "string" } },
                    "required": ["known"]
                  }
                },
                "summary": { "type": "string" }
              },
              "required": ["matched", "items", "summary"]
            }
            """;
        var output = """
            {
              "matched": "yes",
              "items": [{ "known": 42, "model-secret-property": true }],
              "summary": false,
              "model-secret-property": "must stay private"
            }
            """;

        var exception = Assert.Throws<WorkerOutputValidationException>(() =>
            AnalysisWorkerOutputSchemaValidator.Validate(output, schema, "memory"));

        Assert.Equal(5, exception.Violations.Count);
        Assert.Contains(exception.Violations, violation => violation.Contains("output.matched", StringComparison.Ordinal));
        Assert.Contains(exception.Violations, violation => violation.Contains("output.items[0].known", StringComparison.Ordinal));
        Assert.Contains(exception.Violations, violation => violation.Contains("output.summary", StringComparison.Ordinal));
        Assert.Contains(exception.Violations, violation => violation.Contains("1 unsupported properties at output", StringComparison.Ordinal));
        Assert.Contains(exception.Violations, violation => violation.Contains("1 unsupported properties at output.items[0]", StringComparison.Ordinal));
        Assert.DoesNotContain(ModelControlledPropertyName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaValidator_BoundsViolationListAndAddsTruncationMarker()
    {
        const string schema = """
            { "type": "array", "items": { "type": "string" } }
            """;
        var output = "[" + string.Join(",", Enumerable.Range(0, AnalysisWorkerOutputSchemaValidator.MaxReportedViolations + 1)) + "]";

        var exception = Assert.Throws<WorkerOutputValidationException>(() =>
            AnalysisWorkerOutputSchemaValidator.Validate(output, schema, "analysis"));

        Assert.Equal(AnalysisWorkerOutputSchemaValidator.MaxReportedViolations, exception.Violations.Count);
        Assert.True(exception.ViolationsTruncated);
        Assert.Contains(WorkerOutputValidationException.TruncationMarker, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaValidator_MalformedJsonUsesContentFreeDiagnostic()
    {
        const string schema = """{ "type": "object" }""";
        const string modelOutput = "MODEL_RESPONSE_MUST_NOT_ESCAPE {";

        var exception = Assert.Throws<WorkerOutputValidationException>(() =>
            AnalysisWorkerOutputSchemaValidator.Validate(modelOutput, schema, "analysis"));

        Assert.Contains("not valid JSON", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(modelOutput, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaValidator_RejectsTypeUnionInsteadOfTreatingNullAsOptionalString()
    {
        const string schema = """
            {
              "type": "object",
              "properties": { "note": { "type": ["string", "null"] } }
            }
            """;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            AnalysisWorkerOutputSchemaValidator.Validate("{\"note\":null}", schema, "analysis"));

        Assert.Contains("schema is missing string type at output.note", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaValidator_OptionalStringStillRejectsExplicitNull()
    {
        const string schema = """
            {
              "type": "object",
              "properties": { "note": { "type": "string" } }
            }
            """;

        var exception = Assert.Throws<WorkerOutputValidationException>(() =>
            AnalysisWorkerOutputSchemaValidator.Validate("{\"note\":null}", schema, "analysis"));

        Assert.Contains("output.note must be string", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnalysisParser_UsesRoleNameInDiagnostics()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            AnalysisWorkerOutputParser.Parse("{}", "memory"));

        Assert.Contains("memory worker output", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Analysis worker output", exception.Message, StringComparison.Ordinal);
    }
}
