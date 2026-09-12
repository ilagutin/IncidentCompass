using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentCompass.Application.Intake.Configuration;

namespace IncidentCompass.Infrastructure.Intake;

internal sealed class TriageConfigurationMaterializer(TriageConfigurationLoadValidator validator)
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public TriageConfiguration Materialize(
        string configHash,
        JsonNode configNode,
        JsonObject referencesNode)
    {
        var document = Deserialize(configNode);
        var roles = ResolveRoleReferences(RequireDictionary(document.Roles, "Roles"), referencesNode);
        var orchestrator = ResolveOrchestratorReference(RequireValue(document.Orchestrator, "Orchestrator"), referencesNode);
        var rules = NormalizeRules(document.Rules ?? []);

        var configuration = new TriageConfiguration(
            configHash,
            RequireDictionary(document.Providers, "Providers"),
            RequireDictionary(document.Routes, "Routes"),
            orchestrator,
            roles,
            RequireDictionary(document.Tools, "Tools", allowEmpty: true),
            rules,
            RequireValue(document.Ingestion, "Ingestion"),
            RequireValue(document.FaultGrouping, "FaultGrouping"),
            document.Redaction ?? RedactionSettings.Default)
        {
            CurrentReleases = NormalizeCurrentReleases(document.CurrentReleases),
            Actions = document.Actions ?? TriageActionSettings.Default
        };
        validator.Validate(configuration);
        return configuration;
    }

    private static SerializedTriageConfiguration Deserialize(JsonNode configNode)
    {
        try
        {
            using var document = JsonDocument.Parse(configNode.ToJsonString());
            return JsonSerializer.Deserialize<SerializedTriageConfiguration>(document.RootElement, SerializerOptions)!
                ?? throw TriageConfigurationLoadException.InvalidJson("triage configuration", new JsonException("Empty document."));
        }
        catch (JsonException exception)
            when (TryDescribeRejectedReasoning(exception, out var settingName, out var configuredValue))
        {
            throw TriageConfigurationLoadException.InvalidSetting(
                settingName,
                configuredValue,
                "one of: off, low, medium, high");
        }
        catch (JsonException exception)
        {
            throw TriageConfigurationLoadException.InvalidJson("triage configuration", exception);
        }
    }

    /// <summary>
    /// Recovers the setting path and the operator-authored value from a reasoning token that
    /// <see cref="AiReasoningLevelJsonConverter"/> rejected, so the load failure names the value
    /// that was actually configured instead of a placeholder.
    /// </summary>
    private static bool TryDescribeRejectedReasoning(
        JsonException exception,
        out string settingName,
        out string configuredValue)
    {
        settingName = string.Empty;
        configuredValue = string.Empty;

        if (exception.Path is not { } path ||
            !path.StartsWith("$.", StringComparison.Ordinal) ||
            !path.EndsWith(".Reasoning", StringComparison.Ordinal) ||
            exception.Data[AiReasoningLevelJsonConverter.ConfiguredValueKey] is not string rejectedValue)
        {
            return false;
        }

        settingName = path[2..];
        configuredValue = rejectedValue;
        return true;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new AiReasoningLevelJsonConverter());
        return options;
    }

    private static Dictionary<string, T> RequireDictionary<T>(
        IReadOnlyDictionary<string, T>? values,
        string name,
        bool allowEmpty = false)
    {
        if (values is null || (!allowEmpty && values.Count == 0))
        {
            throw TriageConfigurationLoadException.MissingSection(name);
        }

        return values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
    }

    private static T RequireValue<T>(T? value, string name)
        where T : class
    {
        return value ?? throw TriageConfigurationLoadException.MissingSection(name);
    }

    private static Dictionary<string, TriageRoleSettings> ResolveRoleReferences(
        IReadOnlyDictionary<string, TriageRoleSettings> roles,
        JsonObject referencesNode)
    {
        return roles.ToDictionary(
            pair => pair.Key,
            pair => pair.Value with
            {
                Instructions = ResolveReference(pair.Value.Instructions, referencesNode),
                OutputSchema = ResolveReference(pair.Value.OutputSchema, referencesNode)
            },
            StringComparer.Ordinal);
    }

    private static OrchestratorSettings ResolveOrchestratorReference(
        OrchestratorSettings orchestrator,
        JsonObject referencesNode)
    {
        return orchestrator with
        {
            Instructions = ResolveReference(orchestrator.Instructions, referencesNode)
        };
    }

    private static TriageRuleSettings[] NormalizeRules(IReadOnlyCollection<TriageRuleSettings> rules)
    {
        return rules
            .Select(rule => rule with
            {
                Scope = string.IsNullOrWhiteSpace(rule.Scope) ? "attempt" : rule.Scope
            })
            .ToArray();
    }

    private static Dictionary<string, string> NormalizeCurrentReleases(
        IReadOnlyDictionary<string, string>? currentReleases)
    {
        return currentReleases?.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static string ResolveReference(string value, JsonObject referencesNode)
    {
        if (!value.StartsWith("ref:", StringComparison.Ordinal))
        {
            return value;
        }

        if (referencesNode.TryGetPropertyValue(value, out var node) &&
            node is JsonValue jsonValue &&
            jsonValue.GetValueKind() == JsonValueKind.String)
        {
            return jsonValue.GetValue<string>();
        }

        throw TriageConfigurationLoadException.SnapshotReferenceMissing(value);
    }
}
