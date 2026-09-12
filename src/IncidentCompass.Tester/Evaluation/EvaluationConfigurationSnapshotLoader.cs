using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IncidentCompass.Tester.Evaluation;

internal static partial class EvaluationConfigurationSnapshotLoader
{
    public static EvaluationConfigurationSnapshot Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidOperationException("Evaluation configuration is empty.");
        Expand(root);
        var routesNode = root["Routes"]?.AsObject()
            ?? throw new InvalidOperationException("Evaluation configuration has no Routes object.");
        var routes = routesNode.Select(property => ReadRoute(property.Key, property.Value)).ToArray();
        var budget = root["Orchestrator"]?["Budget"]?.AsObject()
            ?? throw new InvalidOperationException("Evaluation configuration has no orchestrator budget.");
        return new EvaluationConfigurationSnapshot(
            routes,
            new EvaluationOrchestratorBudgetResult(
                ReadInt32(budget, "MaxWorkers"),
                ReadInt32(budget, "MaxTokens"),
                ReadInt32(budget, "MaxWallClockSeconds"),
                ReadOptionalInt32(budget, "MaxReprompts")));
    }

    private static EvaluationRouteSettingsResult ReadRoute(string routeId, JsonNode? value)
    {
        var route = value?.AsObject()
            ?? throw new InvalidOperationException("Evaluation route '" + routeId + "' is not an object.");
        return new EvaluationRouteSettingsResult(
            routeId,
            ReadString(route, "Kind"),
            ReadString(route, "ProviderId"),
            ReadString(route, "Model"),
            ReadOptionalDouble(route, "Temperature"),
            ReadOptionalInt32(route, "MaxOutputTokens"),
            ReadOptionalInt32(route, "ContextWindowTokens"));
    }

    private static void Expand(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var key in jsonObject.Select(static property => property.Key).ToArray())
                {
                    ExpandValue(jsonObject, key);
                }
                break;
            case JsonArray jsonArray:
                for (var index = 0; index < jsonArray.Count; index++)
                {
                    ExpandValue(jsonArray, index);
                }
                break;
        }
    }

    private static void ExpandValue(JsonObject parent, string key)
    {
        if (TryExpandString(parent[key], out var expanded))
        {
            parent[key] = expanded;
        }
        else
        {
            Expand(parent[key]);
        }
    }

    private static void ExpandValue(JsonArray parent, int index)
    {
        if (TryExpandString(parent[index], out var expanded))
        {
            parent[index] = expanded;
        }
        else
        {
            Expand(parent[index]);
        }
    }

    private static bool TryExpandString(JsonNode? node, out string expanded)
    {
        expanded = string.Empty;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
        {
            return false;
        }

        expanded = PlaceholderRegex().Replace(value.GetValue<string>(), static match =>
        {
            var configured = Environment.GetEnvironmentVariable(match.Groups["name"].Value);
            if (!string.IsNullOrEmpty(configured))
            {
                return configured;
            }

            if (match.Groups["fallback"].Success)
            {
                return match.Groups["fallback"].Value;
            }

            throw new InvalidOperationException(
                "Environment variable '" + match.Groups["name"].Value + "' is required by evaluation configuration.");
        });
        return true;
    }

    private static string ReadString(JsonObject item, string name) =>
        item[name]?.GetValue<string>()
        ?? throw new InvalidOperationException("Evaluation configuration property '" + name + "' is required.");

    private static int ReadInt32(JsonObject item, string name) =>
        item[name]?.GetValue<int>()
        ?? throw new InvalidOperationException("Evaluation configuration property '" + name + "' is required.");

    private static int? ReadOptionalInt32(JsonObject item, string name) => item[name]?.GetValue<int>();

    private static double? ReadOptionalDouble(JsonObject item, string name) => item[name]?.GetValue<double>();

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(:-(?<fallback>[^}]*))?\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();
}
