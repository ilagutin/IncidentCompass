using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Core.ModelClients;
using IncidentCompass.Application.Core.Serialization;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Governance.Validation;
using IncidentCompass.Domain.Incidents.Actions;

namespace IncidentCompass.IntegrationTests;

internal sealed class SyntheticExternalActionTool(
    Func<Guid, ReadOnlyMemory<byte>, CancellationToken, Task<ExternalActionExecutionResult>>? execute = null,
    ActionCategory category = ActionCategory.Notification,
    string toolId = "action_test",
    string logicalTargetId = "test:target") : IExternalActionTool
{
    private readonly Func<Guid, ReadOnlyMemory<byte>, CancellationToken, Task<ExternalActionExecutionResult>> execute =
        execute ?? DefaultExecuteAsync;
    private int executionCalls;

    public int ExecutionCalls => Volatile.Read(ref executionCalls);

    public Guid? LastActionId { get; private set; }

    public byte[]? LastPayload { get; private set; }

    public ActionCategory Category { get; } = category;

    public string LogicalTargetId { get; } = logicalTargetId;

    public string AdapterBindingFingerprint { get; set; } = ExternalActionBinding.ComputeFingerprint(
        "synthetic", "test:target", "https://api.example.test", "resource-1");

    public AiToolDefinition Definition { get; } = new(
        toolId,
        "Synthetic post-report action.",
        "v1",
        CanonicalJsonSerializer.ToElement(new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["message"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("message")
        }));

    public ToolValidationResult Validate(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            arguments.EnumerateObject().Any(static property => property.Name != "message") ||
            !arguments.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(message.GetString()))
        {
            return ToolValidationResult.Invalid("invalid_arguments", "A message is required.");
        }

        return ToolValidationResult.Valid(JsonSerializer.SerializeToElement(new
        {
            message = message.GetString()!.Trim()
        }));
    }

    public ExternalActionPreparation Prepare(JsonElement sanitizedArguments)
    {
        var payload = new JsonObject
        {
            ["message"] = sanitizedArguments.GetProperty("message").GetString()
        };
        return new ExternalActionPreparation(
            Encoding.UTF8.GetBytes(CanonicalJsonSerializer.Canonicalize(payload)),
            "Send a synthetic bounded notification.");
    }

    public Task<ExternalActionExecutionResult> ExecuteAsync(
        Guid actionId,
        ReadOnlyMemory<byte> canonicalPayload,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref executionCalls);
        LastActionId = actionId;
        LastPayload = canonicalPayload.ToArray();
        return execute(actionId, canonicalPayload, cancellationToken);
    }

    private static Task<ExternalActionExecutionResult> DefaultExecuteAsync(
        Guid actionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ExternalActionExecutionResult(
            true,
            Encoding.UTF8.GetBytes("{\"delivered\":true}"),
            "Synthetic action delivered."));
}
