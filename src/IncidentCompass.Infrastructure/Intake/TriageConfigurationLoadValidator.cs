using System.Globalization;
using System.Text.Json;
using IncidentCompass.Application.Governance.Tools;
using IncidentCompass.Application.Intake.Configuration;
using IncidentCompass.Application.Intake.Normalization;
using IncidentCompass.Application.Investigation.Jobs;
using IncidentCompass.Infrastructure.Configuration;
using static IncidentCompass.Infrastructure.Intake.TriageConfigurationValidationGuards;

namespace IncidentCompass.Infrastructure.Intake;

internal sealed class TriageConfigurationLoadValidator(
    SignalNormalizerRegistry normalizerRegistry,
    IAgentToolRegistry toolRegistry,
    IModelProviderSecretReader secretReader)
{
    private static readonly HashSet<string> RouteKinds = new(["Chat", "Embedding"], StringComparer.Ordinal);
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
        TriageProviderSettingsLoadValidator.Validate(configuration.Providers, secretReader);
        ValidateRoutes(configuration.Providers, configuration.Routes);
        ValidateOrchestrator(configuration.Routes, configuration.Orchestrator);
        ValidateRoles(configuration.Routes, configuration.Tools, configuration.Roles, configuration.Redaction);
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
            RequireDomainReferenceSegment("CurrentReleases." + service, release);
        }
    }

    /// <summary>
    /// A release name and a role key each become one segment of a durable
    /// <c>triage_artifacts.domain_ref</c> - <c>source:{release}:{path}</c> and <c>worker:{role}</c> -
    /// so a value that cannot be a segment is a value every later lookup or delegation that quotes it
    /// would have to refuse. An ISO-8601 release id is the easy way to write one: it carries the
    /// colon the reference separates on, and nothing else in this file would have noticed.
    /// <para>
    /// Checking it here is the earliest reliable layer, which is where
    /// <c>docs/code-organization.md</c> puts a rule like this. The rule itself is not restated: it is
    /// <see cref="ArtifactDomainRef.IsValidSegment" />, so the load boundary and the type cannot
    /// drift apart.
    /// </para>
    /// </summary>
    private static void RequireDomainReferenceSegment(string settingName, string value)
    {
        if (ArtifactDomainRef.IsValidSegment(value))
        {
            return;
        }

        throw Invalid(
            settingName,
            value,
            "a value usable as an artifact domain reference segment: no '" + ArtifactDomainRef.Separator +
                "', no control, format or non-space whitespace character, and at most " +
                ArtifactDomainRef.MaximumSegmentLength.ToString(CultureInfo.InvariantCulture) +
                " characters");
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

        // A second pass, so that every route has already been checked against the provider table
        // before any fallback is resolved. That is what makes "the fallback names a usable provider"
        // hold without being restated here: a route whose ProviderId names no configured provider
        // fails the loop above, whichever route happens to point at it and whatever order the two
        // are declared in.
        foreach (var (routeId, route) in routes)
        {
            ValidateFallbackRoute(routes, routeId, route);
        }
    }

    /// <summary>
    /// Checks a route's declared fallback while the host is starting, so a fallback that could never
    /// answer is rejected at load rather than discovered at the first provider failure - which is the
    /// one moment an operator is least able to act on it, and the moment the declaration exists to
    /// survive.
    /// </summary>
    private static void ValidateFallbackRoute(
        IReadOnlyDictionary<string, TriageRouteSettings> routes,
        string routeId,
        TriageRouteSettings route)
    {
        if (route.FallbackRouteId is null)
        {
            return;
        }

        var settingName = "Routes." + routeId + ".FallbackRouteId";
        RequireNonBlank(settingName, route.FallbackRouteId);

        // Fail-over is executed by the governed chat call path and nothing else reads the
        // declaration, so an embedding route carrying one would be a promise nothing keeps.
        if (!string.Equals(route.Kind, "Chat", StringComparison.Ordinal))
        {
            throw Invalid(settingName, route.FallbackRouteId, "unset for an embedding route");
        }

        if (string.Equals(route.FallbackRouteId, routeId, StringComparison.Ordinal))
        {
            throw Invalid(settingName, route.FallbackRouteId, "a different route id");
        }

        RequireChatRoute(routes, route.FallbackRouteId, settingName);
    }

    private static void ValidateOrchestrator(
        IReadOnlyDictionary<string, TriageRouteSettings> routes,
        OrchestratorSettings orchestrator)
    {
        RequireChatRoute(routes, orchestrator.RouteId, "Orchestrator.RouteId");
        RequireNonBlank("Orchestrator.Instructions", orchestrator.Instructions);
        if (orchestrator.RecoveryInstructions is not null)
        {
            RequireNonBlank("Orchestrator.RecoveryInstructions", orchestrator.RecoveryInstructions);
        }

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

        ValidateAttemptDuration(orchestrator.Budget);

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

        RequireBudgetInRange(
            OrchestratorBudgetSettings.MaxEquivalentCallsSettingName,
            orchestrator.Budget.MaxEquivalentCalls,
            OrchestratorBudgetSettings.MinimumMaxEquivalentCalls,
            OrchestratorBudgetSettings.MaximumMaxEquivalentCalls);
        RequireBudgetInRange(
            OrchestratorBudgetSettings.MaxTurnsWithoutProgressSettingName,
            orchestrator.Budget.MaxTurnsWithoutProgress,
            OrchestratorBudgetSettings.MinimumMaxTurnsWithoutProgress,
            OrchestratorBudgetSettings.MaximumMaxTurnsWithoutProgress);
        RequireBudgetInRange(
            OrchestratorBudgetSettings.MaxRecoveriesSettingName,
            orchestrator.Budget.MaxRecoveries,
            OrchestratorBudgetSettings.MinimumMaxRecoveries,
            OrchestratorBudgetSettings.MaximumMaxRecoveries);
    }

    private static void RequireBudgetInRange(string settingName, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            throw Invalid(
                settingName,
                value.ToString(CultureInfo.InvariantCulture),
                FormattableString.Invariant($"an integer between {minimum} and {maximum}"));
        }
    }

    /// <summary>
    /// The attempt duration ceiling may be spelled with the current key or the deprecated one, never
    /// both: two values would leave the operator guessing which one governs. The deprecated key keeps
    /// its original rule, a positive integer, so an existing configuration and every stored snapshot
    /// carrying it loads unchanged; only the current key can disable the ceiling with <c>0</c>.
    /// </summary>
    private static void ValidateAttemptDuration(OrchestratorBudgetSettings budget)
    {
        if (budget is { MaxAttemptDurationSeconds: { } current, MaxWallClockSeconds: { } legacy })
        {
            throw Invalid(
                OrchestratorBudgetSettings.MaxAttemptDurationSecondsSettingName,
                FormattableString.Invariant($"{current} (with {OrchestratorBudgetSettings.MaxWallClockSecondsSettingName}={legacy})"),
                "only one of " + OrchestratorBudgetSettings.MaxAttemptDurationSecondsSettingName + " and the deprecated " +
                OrchestratorBudgetSettings.MaxWallClockSecondsSettingName);
        }

        if (budget.MaxWallClockSeconds is <= 0)
        {
            throw Invalid(
                OrchestratorBudgetSettings.MaxWallClockSecondsSettingName,
                budget.MaxWallClockSeconds.Value.ToString(CultureInfo.InvariantCulture),
                "a positive integer");
        }

        if (budget.MaxAttemptDurationSeconds is < OrchestratorBudgetSettings.DisabledMaxAttemptDurationSeconds or > OrchestratorBudgetSettings.MaximumMaxAttemptDurationSeconds)
        {
            throw Invalid(
                OrchestratorBudgetSettings.MaxAttemptDurationSecondsSettingName,
                budget.MaxAttemptDurationSeconds.Value.ToString(CultureInfo.InvariantCulture),
                FormattableString.Invariant(
                    $"0 to disable the ceiling, or an integer between 1 and {OrchestratorBudgetSettings.MaximumMaxAttemptDurationSeconds}"));
        }
    }

    private static void ValidateRoles(
        IReadOnlyDictionary<string, TriageRouteSettings> routes,
        IReadOnlyDictionary<string, TriageToolSettings> tools,
        IReadOnlyDictionary<string, TriageRoleSettings> roles,
        RedactionSettings redaction)
    {
        foreach (var (roleName, role) in roles)
        {
            RequireKey(roleName, "Roles");
            RequireDomainReferenceSegment("Roles", roleName);
            RequireChatRoute(routes, role.RouteId, "Roles." + roleName + ".RouteId");
            RequireNonBlank("Roles." + roleName + ".Instructions", role.Instructions);
            RequireNonBlank("Roles." + roleName + ".OutputSchema", role.OutputSchema);
            ValidateOutputSchema(roleName, role.OutputSchema, redaction);
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

    private static void ValidateOutputSchema(string roleName, string outputSchema, RedactionSettings redaction)
    {
        try
        {
            using var document = JsonDocument.Parse(outputSchema);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("Roles." + roleName + ".OutputSchema", "non-object", "a JSON object schema");
            }

            RoleOutputSchemaSecretPropertyLoadValidator.Validate(roleName, document.RootElement, redaction);
        }
        catch (JsonException exception)
        {
            throw TriageConfigurationLoadException.InvalidJson("Roles." + roleName + ".OutputSchema", exception);
        }
    }
}
