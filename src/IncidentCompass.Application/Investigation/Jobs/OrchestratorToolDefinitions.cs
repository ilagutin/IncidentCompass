using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Investigation.Reports;

namespace IncidentCompass.Application.Investigation.Jobs;

internal static class OrchestratorToolDefinitions
{
    public static IReadOnlyList<AiToolDefinition> Create(TriageConfiguration configuration)
    {
        return [CreateDelegateTool(configuration.Roles.Keys), CreatePublishReportTool()];
    }

    private static AiToolDefinition CreateDelegateTool(IEnumerable<string> roles)
    {
        var roleEnum = new JsonArray();
        foreach (var role in roles.Order(StringComparer.Ordinal))
        {
            roleEnum.Add(role);
        }
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["role"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = roleEnum
                },
                ["task"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("role", "task")
        };

        return new AiToolDefinition(
            OrchestratorToolNames.Delegate,
            "Delegate one bounded task to a configured worker role and wait for its result.",
            "v1",
            CanonicalJsonSerializer.ToElement(schema));
    }

    private static AiToolDefinition CreatePublishReportTool()
    {
        var evidenceItemSchema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = true,
            ["properties"] = new JsonObject
            {
                ["referenceId"] = new JsonObject { ["type"] = "string" },
                ["quote"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("referenceId")
        };
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["report_json"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = true,
                    ["properties"] = new JsonObject
                    {
                        ["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Completed", "InsufficientEvidence") },
                        ["summary"] = new JsonObject { ["type"] = "string" },
                        ["classification"] = new JsonObject { ["type"] = "string", ["enum"] = TriageClassificationVocabulary.ToJsonArray() },
                        ["confidence"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Low", "Medium", "High") },
                        ["documentationFit"] = new JsonObject { ["type"] = "string", ["enum"] = CreateDocumentationFitEnum() },
                        ["evidence"] = new JsonObject { ["type"] = "array", ["items"] = evidenceItemSchema },
                        ["limitations"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                        ["recommendedNextAction"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray("status", "summary", "classification", "confidence", "documentationFit", "evidence", "limitations", "recommendedNextAction")
                }
            },
            ["required"] = new JsonArray("report_json")
        };

        return new AiToolDefinition(
            OrchestratorToolNames.PublishReport,
            "Publish the final grounded triage report and end this investigation.",
            "v1",
            CanonicalJsonSerializer.ToElement(schema));
    }

    /// <summary>
    /// The enumeration is generated from <see cref="DocumentationFitStatus"/> itself rather than
    /// written out, so the values the model is handed are the values
    /// <c>TriageReportParser.ReadDocumentationFit</c> parses, by construction.
    /// </summary>
    private static JsonArray CreateDocumentationFitEnum()
    {
        var values = new JsonArray();
        foreach (var name in Enum.GetNames<DocumentationFitStatus>())
        {
            values.Add(name);
        }

        return values;
    }
}
