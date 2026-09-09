using System.Globalization;
using System.Text.Json;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Investigation.Jobs;
using static IncidentCompass.Infrastructure.Intake.TriageConfigurationValidationGuards;

namespace IncidentCompass.Infrastructure.Intake;

internal sealed class TriageConfigurationLoadValidator(
    SignalNormalizerRegistry normalizerRegistry,
    IAgentToolRegistry toolRegistry)
{
    private static readonly HashSet<string> RouteKinds = new(["Chat", "Embedding"], StringComparer.Ordinal);
    private static readonly HashSet<string> ProviderKinds = new(["Mock", "OpenAICompatible"], StringComparer.Ordinal);
    private const string MemoryRoleName = "memory";
    private const string MemorySearchToolName = "memory_search";
    private static readonly HashSet<string> OrchestratorTools = new(OrchestratorToolNames.All, StringComparer.Ordinal);
    private readonly TriageToolConfigurationLoadValidator toolValidator = new(toolRegistry);

    public void Validate(TriageConfiguration configuration)
    {
        FaultGroupingSettingsLoadValidator.Validate(configuration.FaultGrouping);
        RedactionSettingsLoadValidator.Validate(configuration.Redaction);
        ValidateCurrentReleases(configuration.CurrentReleases);
        ValidateAllowedSources(configuration.Ingestion);
        ValidateProviders(configuration.Providers);
        ValidateRoutes(configuration.Providers, configuration.Routes);
        ValidateOrchestrator(configuration.Routes, configuration.Orchestrator);
        ValidateRoles(configuration.Routes, configuration.Tools, configuration.Roles);
        toolValidator.Validate(configuration.Routes, configuration.Tools, configuration.Actions);
        TriageRuleLoadValidator.Validate(configuration.Tools, configuration.Rules);
    }
    private void ValidateAllowedSources(IngestionSettings settings)
    {
        foreach (var source in settings.AllowedSources)
        {
            if (!normalizerRegistry.HasNormalizer(source))
            {
                throw Invalid("Ingestion.AllowedSources", source, "only source kinds with registered normalizers");
            }
        }
    }
    private static void ValidateCurrentReleases(IReadOnlyDictionary<string, string> currentReleases)
    {
        foreach (var (service, release) in currentReleases)
        {
            RequireKey(service, "CurrentReleases");
            RequireNonBlank("CurrentReleases." + service, release);
        }
    }
    private static void ValidateProviders(IReadOnlyDictionary<string, TriageProviderSettings> providers)
    {
        foreach (var (providerId, provider) in providers)
        {
            RequireKey(providerId, "Providers");
            RequireKnown("Providers." + providerId + ".Kind", provider.Kind, ProviderKinds);
        }
    }
    private static void ValidateRoutes(
        IReadOnlyDictionary<string, TriageProviderSettings> providers,
        IReadOnlyDictionary<string, TriageRouteSettings> routes)
    {
        foreach (var (routeId, route) in routes)
        {
            RequireKey(routeId, "Routes");
            RequireKnown("Routes." + routeId + ".Kind", route.Kind, RouteKinds);
            RequireNonBlank("Routes." + routeId + ".ProviderId", route.ProviderId);
            RequireNonBlank("Routes." + routeId + ".Model", route.Model);
            if (!providers.ContainsKey(route.ProviderId))
            {
                throw Invalid("Routes." + routeId + ".ProviderId", route.ProviderId, "a configured provider id");
            }

            if (route.MaxOutputTokens is <= 0)
            {
                throw Invalid("Routes." + routeId + ".MaxOutputTokens", route.MaxOutputTokens.Value.ToString(CultureInfo.InvariantCulture), "a positive integer when set");
            }

            if (route.ContextWindowTokens is <= 0)
            {
                throw Invalid("Routes." + routeId + ".ContextWindowTokens", route.ContextWindowTokens.Value.ToString(CultureInfo.InvariantCulture), "a positive integer when set");
            }

            if (route.Reasoning is { } reasoning && !Enum.IsDefined(reasoning))
            {
                throw Invalid(
                    "Routes." + routeId + ".Reasoning",
                    ((int)reasoning).ToString(CultureInfo.InvariantCulture),
                    "one of: off, low, medium, high");
            }

            if (string.Equals(route.Kind, "Embedding", StringComparison.Ordinal) && route.Reasoning is not null)
            {
                throw Invalid(
                    "Routes." + routeId + ".Reasoning",
                    route.Reasoning.Value.ToString(),
                    "unset for an embedding route");
            }
        }
    }
    private static void ValidateOrchestrator(
        IReadOnlyDictionary<string, TriageRouteSettings> routes,
        OrchestratorSettings orchestrator)
    {
        RequireChatRoute(routes, orchestrator.RouteId, "Orchestrator.RouteId");
        RequireNonBlank("Orchestrator.Instructions", orchestrator.Instructions);

        var tools = orchestrator.Tools.ToHashSet(StringComparer.Ordinal);
        if (tools.Count != OrchestratorTools.Count || !tools.SetEquals(OrchestratorTools))
        {
            throw Invalid("Orchestrator.Tools", string.Join(",", orchestrator.Tools), "exactly: " + string.Join(", ", OrchestratorToolNames.All));
        }

        if (orchestrator.Budget.MaxWorkers <= 0)
        {
            throw Invalid("Orchestrator.Budget.MaxWorkers", orchestrator.Budget.MaxWorkers.ToString(CultureInfo.InvariantCulture), "a positive integer");
        }

        if (orchestrator.Budget.MaxTokens <= 0)
        {
            throw Invalid("Orchestrator.Budget.MaxTokens", orchestrator.Budget.MaxTokens.ToString(CultureInfo.InvariantCulture), "a positive integer");
        }

        if (orchestrator.Budget.MaxWallClockSeconds <= 0)
        {
            throw Invalid("Orchestrator.Budget.MaxWallClockSeconds", orchestrator.Budget.MaxWallClockSeconds.ToString(CultureInfo.InvariantCulture), "a positive integer");
        }

        if (orchestrator.Budget.MaxReprompts < 0)
        {
            throw Invalid("Orchestrator.Budget.MaxReprompts", orchestrator.Budget.MaxReprompts.ToString(CultureInfo.InvariantCulture), "zero or a positive integer");
        }

        if (orchestrator.Budget.MaxTurns is < OrchestratorBudgetSettings.MinimumMaxTurns or > OrchestratorBudgetSettings.MaximumMaxTurns)
        {
            throw Invalid(
                "Orchestrator.Budget.MaxTurns",
                orchestrator.Budget.MaxTurns.ToString(CultureInfo.InvariantCulture),
                FormattableString.Invariant(
                    $"an integer between {OrchestratorBudgetSettings.MinimumMaxTurns} and {OrchestratorBudgetSettings.MaximumMaxTurns}"));
        }
    }

    private static void ValidateRoles(
        IReadOnlyDictionary<string, TriageRouteSettings> routes,
        IReadOnlyDictionary<string, TriageToolSettings> tools,
        IReadOnlyDictionary<string, TriageRoleSettings> roles)
    {
        foreach (var (roleName, role) in roles)
        {
            RequireKey(roleName, "Roles");
            RequireChatRoute(routes, role.RouteId, "Roles." + roleName + ".RouteId");
            RequireNonBlank("Roles." + roleName + ".Instructions", role.Instructions);
            RequireNonBlank("Roles." + roleName + ".OutputSchema", role.OutputSchema);
            ValidateOutputSchema(roleName, role.OutputSchema);
            foreach (var toolName in role.Tools)
            {
                if (!tools.ContainsKey(toolName))
                {
                    throw Invalid("Roles." + roleName + ".Tools", toolName, "a configured worker tool id");
                }
                if (string.Equals(tools[toolName].Kind, "external_action", StringComparison.Ordinal))
                {
                    throw Invalid("Roles." + roleName + ".Tools", toolName, "an immediate internal tool id");
                }
                if (string.Equals(toolName, MemorySearchToolName, StringComparison.Ordinal) &&
                    !string.Equals(roleName, MemoryRoleName, StringComparison.Ordinal))
                {
                    throw Invalid("Roles." + roleName + ".Tools", toolName, "memory_search granted only to the memory role");
                }
                if (string.Equals(toolName, "source_lookup", StringComparison.Ordinal) &&
                    !string.Equals(roleName, "source", StringComparison.Ordinal))
                {
                    throw Invalid("Roles." + roleName + ".Tools", toolName, "source_lookup granted only to the source role");
                }
                if (string.Equals(toolName, "ticket_search", StringComparison.Ordinal) &&
                    !string.Equals(roleName, "tickets", StringComparison.Ordinal))
                {
                    throw Invalid("Roles." + roleName + ".Tools", toolName, "ticket_search granted only to the tickets role");
                }
            }
        }
    }

    private static void ValidateOutputSchema(string roleName, string outputSchema)
    {
        try
        {
            using var document = JsonDocument.Parse(outputSchema);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("Roles." + roleName + ".OutputSchema", "non-object", "a JSON object schema");
            }
        }
        catch (JsonException exception)
        {
            throw TriageConfigurationLoadException.InvalidJson("Roles." + roleName + ".OutputSchema", exception);
        }
    }
}
